// ReFS block-clone engine.
// Port of refsblockclone.fixed.ps1 (original by Sergey Gruzdov, egel@egel.su),
// with these fixes: cluster-aligned clone + EOF shrink, same-volume check,
// delete-pending-first cleanup, sparse output, and Win32 error capture.
using System;
using System.Runtime.InteropServices;

namespace ReFsBlockClone
{
    // ---- Native structures matching the FSCTL buffer layouts ----

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FSCTL_GET_INTEGRITY_INFORMATION_BUFFER
    {
        public ushort ChecksumAlgorithm;
        public ushort Reserved;
        public uint Flags;
        public uint ChecksumChunkSizeInBytes;
        public uint ClusterSizeInBytes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FSCTL_SET_INTEGRITY_INFORMATION_BUFFER
    {
        public ushort ChecksumAlgorithm;
        public ushort Reserved;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FILE_DISPOSITION_INFO
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile; // native BOOLEAN = 1 byte
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FILE_END_OF_FILE_INFO
    {
        public ulong EndOfFile; // LARGE_INTEGER
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DUPLICATE_EXTENTS_DATA
    {
        public long FileHandle; // 64-bit only: HANDLE is 8 bytes on x64
        public ulong SourceFileOffset;
        public ulong TargetFileOffset;
        public ulong ByteCount;
    }

    internal static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint DELETE = 0x00010000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint OPEN_EXISTING = 3;
        public const uint CREATE_NEW = 1;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public const uint FILE_SUPPORTS_BLOCK_REFCOUNTING = 0x08000000;

        public const uint FSCTL_GET_INTEGRITY_INFORMATION = 0x0009027C;
        public const uint FSCTL_SET_INTEGRITY_INFORMATION = 0x0009C280;
        public const uint FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x00098344;
        public const uint FSCTL_SET_SPARSE = 0x000900C4;

        public const int FileEndOfFileInfo = 6;
        public const int FileDispositionInfo = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformationByHandleW(
            IntPtr hFile, IntPtr lpVolumeNameBuffer, uint nVolumeNameSize,
            out uint lpVolumeSerialNumber, IntPtr lpMaximumComponentLength,
            out uint lpFileSystemFlags, IntPtr lpFileSystemNameBuffer,
            uint nFileSystemNameSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileSizeEx(IntPtr hFile, out ulong lpFileSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileInformationByHandle(
            IntPtr hFile, int FileInformationClass, IntPtr lpFileInformation,
            uint dwBufferSize);
    }

    public sealed class CloneException : Exception
    {
        public CloneException(string message) : base(message) { }
    }

    /// <summary>
    /// Block-clone engine. FSCTL_DUPLICATE_EXTENTS_TO_FILE copies physical block
    /// references (refcounts) without touching any data bytes, so cloning is a
    /// pure metadata operation: near-zero NAND writes, near-zero space use, and
    /// effectively instant for files of any size.
    /// </summary>
    public sealed class RefsBlockCloner
    {
        private readonly Action<string> _log;

        public RefsBlockCloner(Action<string> log) { _log = log; }

        public void Clone(string inFile, string outFile)
        {
            IntPtr hIn = IntPtr.Zero;
            IntPtr hOut = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            try
            {
                // Open the source read-only (shared read).
                _log("打开源文件...");
                hIn = NativeMethods.CreateFileW(inFile, NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ, IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                if (hIn == NativeMethods.INVALID_HANDLE_VALUE)
                    throw WinErr(Marshal.GetLastWin32Error(), "无法打开源文件：" + inFile);

                // The source volume must support block refcounting (ReFS).
                uint srcFlags; uint srcSerial;
                if (!NativeMethods.GetVolumeInformationByHandleW(hIn, IntPtr.Zero, 0,
                        out srcSerial, IntPtr.Zero, out srcFlags, IntPtr.Zero, 0))
                    throw WinErr(Marshal.GetLastWin32Error(), "无法获取源卷信息");
                _log("源卷支持块克隆（卷序列号 0x" + srcSerial.ToString("X8") + "）");

                if ((srcFlags & NativeMethods.FILE_SUPPORTS_BLOCK_REFCOUNTING) == 0)
                    throw new CloneException("源卷不支持块克隆（非 ReFS 或版本过旧）。");

                ulong srcSize;
                if (!NativeMethods.GetFileSizeEx(hIn, out srcSize))
                    throw WinErr(Marshal.GetLastWin32Error(), "无法获取源文件大小");

                // CREATE_NEW never overwrites an existing destination.
                _log("创建目标文件...");
                hOut = NativeMethods.CreateFileW(outFile,
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE | NativeMethods.DELETE,
                    0, IntPtr.Zero, NativeMethods.CREATE_NEW, 0, IntPtr.Zero);
                if (hOut == NativeMethods.INVALID_HANDLE_VALUE)
                    throw WinErr(Marshal.GetLastWin32Error(), "无法创建目标文件：" + outFile);

                // Mark the output sparse so sparse sources stay sparse
                // (harmless for dense files).
                uint dummy;
                NativeMethods.DeviceIoControl(hOut, NativeMethods.FSCTL_SET_SPARSE,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero);

                // Set delete-pending FIRST: any later failure auto-deletes the
                // partial output when the handle is closed, so no orphan remains.
                var disp = new FILE_DISPOSITION_INFO { DeleteFile = true };
                int sizeDisp = Marshal.SizeOf(typeof(FILE_DISPOSITION_INFO));
                buf = Marshal.AllocHGlobal(sizeDisp);
                Marshal.StructureToPtr(disp, buf, false);
                if (!NativeMethods.SetFileInformationByHandle(hOut,
                        NativeMethods.FileDispositionInfo, buf, (uint)sizeDisp))
                    throw WinErr(Marshal.GetLastWin32Error(), "无法设置删除待决标记");
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;

                // The destination must support block cloning and be on the SAME
                // volume as the source.
                uint dstFlags; uint dstSerial;
                if (!NativeMethods.GetVolumeInformationByHandleW(hOut, IntPtr.Zero, 0,
                        out dstSerial, IntPtr.Zero, out dstFlags, IntPtr.Zero, 0))
                    throw WinErr(Marshal.GetLastWin32Error(), "无法获取目标卷信息");

                if ((dstFlags & NativeMethods.FILE_SUPPORTS_BLOCK_REFCOUNTING) == 0)
                    throw new CloneException("目标卷不支持块克隆，目标须与源位于同一 ReFS 卷。");

                if (srcSerial != dstSerial)
                    throw new CloneException("源与目标必须位于同一 ReFS 卷（卷序列号不匹配）。");

                // Read the source integrity info to learn the volume cluster size.
                var gi = new FSCTL_GET_INTEGRITY_INFORMATION_BUFFER();
                int sizeGi = Marshal.SizeOf(typeof(FSCTL_GET_INTEGRITY_INFORMATION_BUFFER));
                buf = Marshal.AllocHGlobal(sizeGi);
                if (!NativeMethods.DeviceIoControl(hIn, NativeMethods.FSCTL_GET_INTEGRITY_INFORMATION,
                        IntPtr.Zero, 0, buf, (uint)sizeGi, out dummy, IntPtr.Zero))
                {
                    Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                    throw WinErr(Marshal.GetLastWin32Error(), "无法获取源文件完整性信息");
                }
                gi = (FSCTL_GET_INTEGRITY_INFORMATION_BUFFER)Marshal.PtrToStructure(buf, typeof(FSCTL_GET_INTEGRITY_INFORMATION_BUFFER));
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;

                ulong cluster = gi.ClusterSizeInBytes;
                if (cluster == 0) cluster = 4096;

                // DUPLICATE_EXTENTS requires cluster-aligned offsets/lengths, so
                // round the clone extent UP to the cluster boundary when the source
                // size is not a multiple of the cluster size.
                ulong alignedSize = ((srcSize + cluster - 1) / cluster) * cluster;
                _log(string.Format("簇大小 {0} 字节，逻辑大小 {1} 字节，对齐大小 {2} 字节",
                    cluster, srcSize, alignedSize));

                // The output EOF must be large enough to hold the aligned clone.
                var eof = new FILE_END_OF_FILE_INFO { EndOfFile = alignedSize };
                int sizeEof = Marshal.SizeOf(typeof(FILE_END_OF_FILE_INFO));
                buf = Marshal.AllocHGlobal(sizeEof);
                Marshal.StructureToPtr(eof, buf, false);
                if (!NativeMethods.SetFileInformationByHandle(hOut,
                        NativeMethods.FileEndOfFileInfo, buf, (uint)sizeEof))
                {
                    Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                    throw WinErr(Marshal.GetLastWin32Error(), "无法设置目标文件大小");
                }
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;

                // Copy the integrity setting from source to output.
                var si = new FSCTL_SET_INTEGRITY_INFORMATION_BUFFER
                {
                    ChecksumAlgorithm = gi.ChecksumAlgorithm,
                    Reserved = gi.Reserved,
                    Flags = gi.Flags
                };
                int sizeSi = Marshal.SizeOf(typeof(FSCTL_SET_INTEGRITY_INFORMATION_BUFFER));
                buf = Marshal.AllocHGlobal(sizeSi);
                Marshal.StructureToPtr(si, buf, true);
                bool okSi = NativeMethods.DeviceIoControl(hOut,
                    NativeMethods.FSCTL_SET_INTEGRITY_INFORMATION, buf, (uint)sizeSi,
                    IntPtr.Zero, 0, out dummy, IntPtr.Zero);
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                if (!okSi)
                    throw WinErr(Marshal.GetLastWin32Error(), "无法设置目标文件完整性信息");

                // Clone loop in 100 MB chunks (all offsets/lengths are cluster multiples).
                var dup = new DUPLICATE_EXTENTS_DATA { FileHandle = hIn.ToInt64() };
                int sizeDup = Marshal.SizeOf(typeof(DUPLICATE_EXTENTS_DATA));
                buf = Marshal.AllocHGlobal(sizeDup);
                const ulong CHUNK = 100UL * 1024 * 1024;
                ulong offset = 0;
                while (offset < alignedSize)
                {
                    ulong bc = Math.Min(CHUNK, alignedSize - offset);
                    dup.SourceFileOffset = offset;
                    dup.TargetFileOffset = offset;
                    dup.ByteCount = bc;
                    Marshal.StructureToPtr(dup, buf, false);
                    if (!NativeMethods.DeviceIoControl(hOut,
                            NativeMethods.FSCTL_DUPLICATE_EXTENTS_TO_FILE,
                            buf, (uint)sizeDup, IntPtr.Zero, 0, out dummy, IntPtr.Zero))
                    {
                        Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                        throw WinErr(Marshal.GetLastWin32Error(),
                            string.Format("块克隆失败：偏移 {0}", offset));
                    }
                    offset += bc;
                    _log(string.Format("  已克隆 {0}/{1} 字节", offset, alignedSize));
                }
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;

                // Shrink the output back to the exact source size so the clone is
                // byte-identical to the source.
                if (alignedSize > srcSize)
                {
                    var eof2 = new FILE_END_OF_FILE_INFO { EndOfFile = srcSize };
                    buf = Marshal.AllocHGlobal(sizeEof);
                    Marshal.StructureToPtr(eof2, buf, false);
                    bool okShrink = NativeMethods.SetFileInformationByHandle(hOut,
                        NativeMethods.FileEndOfFileInfo, buf, (uint)sizeEof);
                    Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                    if (!okShrink)
                        throw WinErr(Marshal.GetLastWin32Error(), "无法收缩目标到源文件大小");
                    _log("已收缩到源文件精确大小。");
                }

                // Clear delete-pending so the output file survives.
                var disp2 = new FILE_DISPOSITION_INFO { DeleteFile = false };
                buf = Marshal.AllocHGlobal(sizeDisp);
                Marshal.StructureToPtr(disp2, buf, false);
                bool okClear = NativeMethods.SetFileInformationByHandle(hOut,
                    NativeMethods.FileDispositionInfo, buf, (uint)sizeDisp);
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                if (!okClear)
                    throw WinErr(Marshal.GetLastWin32Error(), "无法清除删除待决标记");

                _log("克隆完成。");
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (hIn != IntPtr.Zero && hIn != NativeMethods.INVALID_HANDLE_VALUE)
                    NativeMethods.CloseHandle(hIn);
                if (hOut != IntPtr.Zero && hOut != NativeMethods.INVALID_HANDLE_VALUE)
                    NativeMethods.CloseHandle(hOut); // delete-pending still set -> partial output removed
            }
        }

        private static Win32ExceptionEx WinErr(int code, string what)
        {
            return new Win32ExceptionEx(code, what);
        }
    }

    /// <summary>Exception carrying the raw Win32 error code with a formatted message.</summary>
    public sealed class Win32ExceptionEx : Exception
    {
        public int NativeErrorCode { get; private set; }

        public Win32ExceptionEx(int code, string what)
            : base(string.Format("{0}（Win32 错误 0x{1:X}）", what, (uint)code))
        {
            NativeErrorCode = code;
        }
    }
}

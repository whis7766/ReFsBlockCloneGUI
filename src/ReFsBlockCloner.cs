// ReFS block-clone engine.
//
// Corrected against Microsoft primary sources (see docs/DESIGN.md):
//   * [MS-FSA] 2.1.5.10.4 vs [MS-FSCC] 2.3.7: source-range validation口径
//     differs (logical EOF vs allocation size); real ReFS 2025 accepts the
//     allocation-size reading. We clone the cluster-aligned PREFIX via
//     FSCTL_DUPLICATE_EXTENTS_TO_FILE and copy the trailing partial cluster
//     with ordinary I/O, which is correct under BOTH readings.
//   * Block cloning restrictions: cluster-aligned offsets/lengths, < 4 GB per
//     call, same ReFS volume, identical Integrity Streams settings, sparse
//     source => sparse target, destination region within EOF (SetEndOfFile
//     first), max 8175 references per physical region (error 347).
//   * Sparse attribute, integrity streams, timestamps/attributes, named
//     alternate data streams (ordinary I/O) and the owner/group/DACL security
//     descriptor are MIRRORED from the source.
//   * Delete-pending is a process-level cleanup aid armed immediately after
//     CREATE_NEW (not a clone prerequisite, not crash-persistent); a
//     DeleteFileW fallback covers the window before it is armed.
//   * FSCTL_DUPLICATE_EXTENTS_TO_FILE_EX + SOURCE_ATOMIC is deliberately NOT
//     used: its per-call source atomicity overlaps our cleanup guarantees and
//     adds source-side serialization cost (see DESIGN.md audit).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

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
        public IntPtr FileHandle; // HANDLE: pointer-sized (8 bytes on x64)
        public ulong SourceFileOffset;
        public ulong TargetFileOffset;
        public ulong ByteCount;
    }

    // Natural alignment (4 x LARGE_INTEGER + DWORD => 40 bytes with padding).
    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FILE_ALLOCATED_RANGE_BUFFER
    {
        public long FileOffset;
        public long Length;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260 + 36)]
        public string StreamName;
    }

    internal static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint DELETE = 0x00010000;
        public const uint WRITE_DAC = 0x00040000;
        public const uint WRITE_OWNER = 0x00080000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint FILE_SHARE_DELETE = 0x00000004;
        public const uint OPEN_EXISTING = 3;
        public const uint CREATE_NEW = 1;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public const uint FILE_SUPPORTS_BLOCK_REFCOUNTING = 0x08000000;

        public const uint FSCTL_GET_INTEGRITY_INFORMATION = 0x0009027C;
        public const uint FSCTL_SET_INTEGRITY_INFORMATION = 0x0009C280;
        public const uint FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x00098344;
        public const uint FSCTL_SET_SPARSE = 0x000900C4;
        public const uint FSCTL_QUERY_ALLOCATED_RANGES = 0x000940CF;

        public const int FileBasicInfo = 0;
        public const int FileEndOfFileInfo = 6;
        public const int FileDispositionInfo = 4;

        public const uint FILE_BEGIN = 0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteFileW(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFileAttributesW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformationByHandleW(
            IntPtr hFile, IntPtr lpVolumeNameBuffer, uint nVolumeNameSize,
            out uint lpVolumeSerialNumber, IntPtr lpMaximumComponentLength,
            out uint lpFileSystemFlags, IntPtr lpFileSystemNameBuffer,
            uint nFileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformationByHandleW(
            IntPtr hFile, IntPtr lpVolumeNameBuffer, uint nVolumeNameSize,
            out uint lpVolumeSerialNumber, IntPtr lpMaximumComponentLength,
            out uint lpFileSystemFlags,
            System.Text.StringBuilder lpFileSystemNameBuffer,
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(
            IntPtr hFile, int FileInformationClass, IntPtr lpFileInformation,
            uint dwBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFilePointerEx(
            IntPtr hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(
            IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(
            IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstStreamW(string lpFileName, uint dwInfoLevel,
            ref WIN32_FIND_STREAM_DATA lpFindStreamData, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool FindNextStreamW(IntPtr hFindStream,
            ref WIN32_FIND_STREAM_DATA lpFindStreamData);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FindClose(IntPtr hFindStream);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern uint GetSecurityInfo(
            IntPtr handle, uint ObjectType, uint SecurityInfo,
            out IntPtr ppsidOwner, out IntPtr ppsidGroup, out IntPtr ppDacl,
            IntPtr ppSacl, out IntPtr ppSecurityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern uint SetSecurityInfo(
            IntPtr handle, uint ObjectType, uint SecurityInfo,
            IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr hMem);
    }

    public sealed class CloneException : Exception
    {
        public CloneException(string message) : base(message) { }
    }

    /// <summary>Progress snapshot reported by the clone loop.</summary>
    public struct CloneProgress
    {
        public long BytesDone;
        public long TotalBytes;
        public CloneProgress(long done, long total) { BytesDone = done; TotalBytes = total; }
    }

    /// <summary>Volume/file facts used by the engine and shown in the GUI.</summary>
    public sealed class VolumeInfo
    {
        public string FileSystem = "";
        public uint Serial;
        public bool SupportsBlockClone;
        public uint ClusterSize;
        public bool IntegrityEnabled;
        public bool ChecksumEnforcementOff;
        public bool IsSparse;
        public bool PathExists;
        public bool IsDirectory;
    }

    /// <summary>
    /// Block-clone engine. FSCTL_DUPLICATE_EXTENTS_TO_FILE remaps VCN-&gt;LCN
    /// references (refcounts) without copying data bytes; isolation afterwards
    /// is preserved by ReFS allocate-on-write. Only the trailing partial
    /// cluster of a non-aligned file is copied as ordinary data.
    /// </summary>
    public sealed class RefsBlockCloner
    {
        /// <summary>
        /// Default FSCTL chunk: 1 GB. Far below the 4 GB per-call cap; larger
        /// chunks amortize per-call validation/transaction-commit overhead,
        /// which reduces both elapsed time and metadata/journal write volume
        /// (CI-benchmarked, see docs/DESIGN.md).
        /// </summary>
        public const ulong DefaultChunkBytes = 1UL * 1024 * 1024 * 1024;

        private readonly Action<string> _log;

        public RefsBlockCloner(Action<string> log) { _log = log ?? (s => { }); }

        /// <summary>
        /// Block-clone <paramref name="inFile"/> to <paramref name="outFile"/>.
        /// Both must live on the same ReFS volume; the destination must not exist.
        /// On any failure the partial destination is removed (delete-pending).
        /// </summary>
        /// <param name="chunkBytes">0 = DefaultChunkBytes; must be a cluster
        /// multiple and &lt; 4 GB (benchmark override).</param>
        public void Clone(string inFile, string outFile,
            IProgress<CloneProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken),
            ulong chunkBytes = 0)
        {
            string src = NormalizePath(inFile);
            string dst = NormalizePath(outFile);
            if (src.Length == 0) throw new CloneException("源文件路径为空。");
            if (dst.Length == 0) throw new CloneException("目标文件路径为空。");
            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                throw new CloneException("源文件与目标文件不能相同。");
            if (System.IO.Directory.Exists(src))
                throw new CloneException("源路径是目录，块克隆仅支持文件：" + inFile);
            if (System.IO.Directory.Exists(dst))
                throw new CloneException("目标路径是目录，请更换目标文件名：" + outFile);
            List<AlternateStream> altStreams = System.IO.File.Exists(src)
                ? EnumerateAlternateStreams(src)
                : new List<AlternateStream>();
            ulong chunk = chunkBytes == 0 ? DefaultChunkBytes : chunkBytes;
            if (chunk >= 4UL * 1024 * 1024 * 1024)
                throw new CloneException("分块大小必须小于 4GB（ReFS 单次克隆上限）。");

            IntPtr hIn = IntPtr.Zero;
            IntPtr hOut = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            bool created = false;
            bool dispositionSet = false;
            try
            {
                ulong srcSize; bool srcSparse; ulong cluster; uint srcSerial;
                FSCTL_GET_INTEGRITY_INFORMATION_BUFFER gi;
                OpenSource(src, inFile, out hIn, out srcSize, out srcSparse, out cluster, out srcSerial, out gi);

                ulong prefix = srcSize - (srcSize % cluster); // aligned, cloneable part
                ulong tail = srcSize - prefix;                // partial cluster, ordinary I/O
                if (chunk % cluster != 0)
                    throw new CloneException("分块大小必须是簇大小（" + cluster + " 字节）的整数倍。");
                _log(string.Format("簇大小 {0} 字节，源大小 {1} 字节：块克隆前缀 {2} 字节 + 普通复制尾部 {3} 字节",
                    cluster, srcSize, prefix, tail));

                CreateDestination(dst, outFile, out hOut);
                created = true;
                SetDisposition(hOut, true);
                dispositionSet = true;
                CheckSameVolume(hIn, hOut, srcSerial);
                MirrorSparse(hOut, srcSparse);
                MirrorIntegrity(hOut, gi, true);
                SetEof(hOut, srcSize);

                // ---- block-clone the aligned prefix ----
                var dup = new DUPLICATE_EXTENTS_DATA { FileHandle = hIn };
                int sizeDup = Marshal.SizeOf(typeof(DUPLICATE_EXTENTS_DATA));
                buf = Marshal.AllocHGlobal(sizeDup);
                ulong offset = 0;
                uint dummy;
                while (offset < prefix)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ulong bc = Math.Min(chunk, prefix - offset);
                    dup.SourceFileOffset = offset;
                    dup.TargetFileOffset = offset;
                    dup.ByteCount = bc;
                    Marshal.StructureToPtr(dup, buf, false);
                    if (!NativeMethods.DeviceIoControl(hOut,
                            NativeMethods.FSCTL_DUPLICATE_EXTENTS_TO_FILE,
                            buf, (uint)sizeDup, IntPtr.Zero, 0, out dummy, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                        throw WinErr(err, string.Format("块克隆失败：偏移 {0}", offset));
                    }
                    offset += bc;
                    _log(string.Format("  已块克隆 {0}/{1} 字节", offset, srcSize));
                    if (progress != null) progress.Report(new CloneProgress((long)offset, (long)srcSize));
                }
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;

                // A progress callback may cancel during the last (or only) chunk;
                // re-check before tail/metadata so cancellation always cleans up.
                cancellationToken.ThrowIfCancellationRequested();
                CopyTail(hIn, hOut, prefix, tail, srcSparse);
                cancellationToken.ThrowIfCancellationRequested();
                if (progress != null) progress.Report(new CloneProgress((long)srcSize, (long)srcSize));

                SetDisposition(hOut, false);
                dispositionSet = false;
                CopyAlternateStreams(src, dst, altStreams, cancellationToken);
                CopyMetadata(hIn, hOut);
                // FileBasicInfo cannot carry the sparse flag (it is FSCTL-only);
                // re-assert it in case the attribute write dropped it.
                if (srcSparse)
                {
                    uint dummy;
                    if (!NativeMethods.DeviceIoControl(hOut, NativeMethods.FSCTL_SET_SPARSE,
                            IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero))
                        _log("警告：重新标记目标稀疏失败（Win32 " + Marshal.GetLastWin32Error() + "）。");
                }
                _log("克隆完成。");
            }
            catch
            {
                CleanupUnarmed(created, dispositionSet, ref hOut, dst);
                throw;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                CloseIfValid(hIn);
                CloseIfValid(hOut); // delete-pending still armed -> partial removed
            }
        }

        // ---- shared pipeline steps ----

        private void OpenSource(string src, string display, out IntPtr hIn,
            out ulong srcSize, out bool srcSparse, out ulong cluster, out uint srcSerial,
            out FSCTL_GET_INTEGRITY_INFORMATION_BUFFER gi)
        {
            _log("打开源文件...");
            hIn = NativeMethods.CreateFileW(src, NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ, IntPtr.Zero,
                NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (hIn == NativeMethods.INVALID_HANDLE_VALUE)
                throw WinErr(Marshal.GetLastWin32Error(), "无法打开源文件：" + display);

            uint srcFlags;
            if (!NativeMethods.GetVolumeInformationByHandleW(hIn, IntPtr.Zero, 0,
                    out srcSerial, IntPtr.Zero, out srcFlags, IntPtr.Zero, 0))
                throw WinErr(Marshal.GetLastWin32Error(), "无法获取源卷信息");
            if ((srcFlags & NativeMethods.FILE_SUPPORTS_BLOCK_REFCOUNTING) == 0)
                throw new CloneException("源卷不支持块克隆（需要 Windows Server 2016+ 格式化的 ReFS 卷）。");
            _log(string.Format("源卷支持块克隆（卷序列号 0x{0:X8}）", srcSerial));

            if (!NativeMethods.GetFileSizeEx(hIn, out srcSize))
                throw WinErr(Marshal.GetLastWin32Error(), "无法获取源文件大小");
            uint attr = AttributesOfHandle(hIn);
            if (attr == 0xFFFFFFFF) attr = NativeMethods.GetFileAttributesW(src);
            srcSparse = attr != 0xFFFFFFFF && (attr & NativeMethods.FILE_ATTRIBUTE_SPARSE_FILE) != 0;

            gi = new FSCTL_GET_INTEGRITY_INFORMATION_BUFFER();
            int sizeGi = Marshal.SizeOf(typeof(FSCTL_GET_INTEGRITY_INFORMATION_BUFFER));
            IntPtr buf = Marshal.AllocHGlobal(sizeGi);
            uint dummy;
            if (!NativeMethods.DeviceIoControl(hIn, NativeMethods.FSCTL_GET_INTEGRITY_INFORMATION,
                    IntPtr.Zero, 0, buf, (uint)sizeGi, out dummy, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(buf);
                throw WinErr(err, "无法获取源文件完整性信息");
            }
            gi = (FSCTL_GET_INTEGRITY_INFORMATION_BUFFER)Marshal.PtrToStructure(
                buf, typeof(FSCTL_GET_INTEGRITY_INFORMATION_BUFFER));
            Marshal.FreeHGlobal(buf);
            cluster = gi.ClusterSizeInBytes;
            if (cluster == 0) cluster = 4096;
        }

        private void CreateDestination(string dst, string display, out IntPtr hOut)
        {
            _log("创建目标文件...");
            hOut = NativeMethods.CreateFileW(dst,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE | NativeMethods.DELETE |
                NativeMethods.WRITE_DAC | NativeMethods.WRITE_OWNER,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                IntPtr.Zero, NativeMethods.CREATE_NEW, 0, IntPtr.Zero);
            if (hOut == NativeMethods.INVALID_HANDLE_VALUE)
                throw WinErr(Marshal.GetLastWin32Error(), "无法创建目标文件：" + display);
        }

        private void CheckSameVolume(IntPtr hIn, IntPtr hOut, uint srcSerial)
        {
            uint dstFlags, dstSerial;
            if (!NativeMethods.GetVolumeInformationByHandleW(hOut, IntPtr.Zero, 0,
                    out dstSerial, IntPtr.Zero, out dstFlags, IntPtr.Zero, 0))
                throw WinErr(Marshal.GetLastWin32Error(), "无法获取目标卷信息");
            if ((dstFlags & NativeMethods.FILE_SUPPORTS_BLOCK_REFCOUNTING) == 0)
                throw new CloneException("目标卷不支持块克隆，目标须与源位于同一 ReFS 卷。");
            if (srcSerial != dstSerial)
                throw new CloneException("源与目标必须位于同一 ReFS 卷（卷序列号不匹配）。");
        }

        private void MirrorSparse(IntPtr hOut, bool srcSparse)
        {
            if (!srcSparse) return;
            uint dummy;
            if (!NativeMethods.DeviceIoControl(hOut, NativeMethods.FSCTL_SET_SPARSE,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero))
                throw WinErr(Marshal.GetLastWin32Error(), "无法将目标标记为稀疏");
            _log("源为稀疏文件，目标已标记稀疏。");
        }

        private void MirrorIntegrity(IntPtr hOut, FSCTL_GET_INTEGRITY_INFORMATION_BUFFER gi, bool required)
        {
            var si = new FSCTL_SET_INTEGRITY_INFORMATION_BUFFER
            {
                ChecksumAlgorithm = gi.ChecksumAlgorithm,
                Reserved = 0,
                Flags = gi.Flags
            };
            int sizeSi = Marshal.SizeOf(typeof(FSCTL_SET_INTEGRITY_INFORMATION_BUFFER));
            IntPtr buf = Marshal.AllocHGlobal(sizeSi);
            Marshal.StructureToPtr(si, buf, false);
            uint dummy;
            bool ok = NativeMethods.DeviceIoControl(hOut,
                NativeMethods.FSCTL_SET_INTEGRITY_INFORMATION, buf, (uint)sizeSi,
                IntPtr.Zero, 0, out dummy, IntPtr.Zero);
            int err = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(buf);
            if (!ok)
            {
                if (required) throw WinErr(err, "无法设置目标文件完整性信息");
                _log("警告：镜像完整性设置失败（Win32 " + err + "），不影响数据克隆。");
                return;
            }
            _log(gi.ChecksumAlgorithm != 0
                ? "已复制源完整性 streams 设置（启用）。"
                : "已复制源完整性 streams 设置（关闭）。");
        }

        private void CopyTail(IntPtr hIn, IntPtr hOut, ulong prefix, ulong tail, bool srcSparse)
        {
            if (tail == 0) return;
            var data = new byte[tail];
            var tmp = new byte[64 * 1024];
            long np;
            if (!NativeMethods.SetFilePointerEx(hIn, (long)prefix, out np, NativeMethods.FILE_BEGIN))
                throw WinErr(Marshal.GetLastWin32Error(), "无法定位源文件尾部");
            uint got = 0;
            while (got < tail)
            {
                uint want = (uint)Math.Min((ulong)tmp.Length, tail - got);
                uint n;
                if (!NativeMethods.ReadFile(hIn, tmp, want, out n, IntPtr.Zero))
                    throw WinErr(Marshal.GetLastWin32Error(), "无法读取源文件尾部");
                if (n == 0)
                    throw new CloneException("源文件在克隆期间被截断，无法读取尾部。");
                Array.Copy(tmp, 0, data, (int)got, (int)n);
                got += n;
            }
            bool allZero = true;
            for (int i = 0; i < data.Length; i++) if (data[i] != 0) { allZero = false; break; }
            if (srcSparse && allZero)
            {
                _log("尾部为空洞，保持稀疏，不写入。");
                return;
            }
            if (!NativeMethods.SetFilePointerEx(hOut, (long)prefix, out np, NativeMethods.FILE_BEGIN))
                throw WinErr(Marshal.GetLastWin32Error(), "无法定位目标文件尾部");
            uint wr;
            if (!NativeMethods.WriteFile(hOut, data, (uint)tail, out wr, IntPtr.Zero) || wr != tail)
                throw WinErr(Marshal.GetLastWin32Error(), "无法写入目标文件尾部");
            _log(string.Format("尾部 {0} 字节已普通复制。", tail));
        }

        /// <summary>
        /// Mirror timestamps, attributes and owner/group/DACL. Best-effort:
        /// failures are logged as warnings and never fail an otherwise
        /// successful data clone (setting owner/SACL may need privileges).
        /// </summary>
        private void CopyMetadata(IntPtr hIn, IntPtr hOut)
        {
            int sz = Marshal.SizeOf(typeof(FILE_BASIC_INFO));
            IntPtr b = Marshal.AllocHGlobal(sz);
            try
            {
                if (NativeMethods.GetFileInformationByHandleEx(hIn, NativeMethods.FileBasicInfo, b, (uint)sz))
                {
                    var info = (FILE_BASIC_INFO)Marshal.PtrToStructure(b, typeof(FILE_BASIC_INFO));
                    const uint SETTABLE = 0x00000001u | 0x00000002u | 0x00000004u | 0x00000020u |
                                          0x00000080u | 0x00000100u | 0x00001000u | 0x00002000u;
                    uint attrs = info.FileAttributes & SETTABLE;
                    if ((attrs & 0x00000080u) != 0 && (attrs & ~0x00000080u) != 0)
                        attrs &= ~0x00000080u;
                    info.FileAttributes = attrs;
                    Marshal.StructureToPtr(info, b, false);
                    if (!NativeMethods.SetFileInformationByHandle(hOut, NativeMethods.FileBasicInfo, b, (uint)sz))
                        _log("警告：镜像时间戳/属性失败（Win32 " + Marshal.GetLastWin32Error() + "）。");
                }
            }
            finally { Marshal.FreeHGlobal(b); }

            IntPtr sd = IntPtr.Zero, owner = IntPtr.Zero, group = IntPtr.Zero, dacl = IntPtr.Zero;
            const uint SE_FILE_OBJECT = 1;
            const uint OWNER_GROUP_DACL = 1 | 2 | 4;
            const uint PROTECTED_DACL = 0x80000000;
            uint rc = NativeMethods.GetSecurityInfo(hIn, SE_FILE_OBJECT, OWNER_GROUP_DACL,
                out owner, out group, out dacl, IntPtr.Zero, out sd);
            if (rc == 0)
            {
                // Source ACEs carry INHERITED_ACE; if applied verbatim the DEST
                // parent re-derives them (different dir => different ACL). Strip
                // INHERITED_ACE and set a protected DACL so the source's effective
                // entries stick on any destination.
                if (dacl != IntPtr.Zero)
                {
                    int aceCount = Marshal.ReadInt16(dacl, 4);
                    int pos = 8; // ACL header size
                    for (int i = 0; i < aceCount; i++)
                    {
                        byte flags = Marshal.ReadByte(dacl, pos + 1);
                        Marshal.WriteByte(dacl, pos + 1, (byte)(flags & ~0x10));
                        int aceSize = Marshal.ReadInt16(dacl, pos + 2);
                        if (aceSize <= 0) break;
                        pos += aceSize;
                    }
                }
                uint rc2 = NativeMethods.SetSecurityInfo(hOut, SE_FILE_OBJECT,
                    OWNER_GROUP_DACL | PROTECTED_DACL, owner, group, dacl, IntPtr.Zero);
                if (rc2 != 0)
                    _log("警告：镜像安全描述符失败（Win32 " + rc2 + "）。");
                NativeMethods.LocalFree(sd);
            }
            else
            {
                _log("警告：读取源安全描述符失败（Win32 " + rc + "）。");
            }
        }

        private void CleanupUnarmed(bool created, bool dispositionSet, ref IntPtr hOut, string dst)
        {
            // Close the tiny window between CREATE_NEW and delete-pending.
            if (created && !dispositionSet)
            {
                CloseIfValid(hOut);
                hOut = IntPtr.Zero;
                NativeMethods.DeleteFileW(dst);
            }
        }

        private static void CloseIfValid(IntPtr h)
        {
            if (h != IntPtr.Zero && h != NativeMethods.INVALID_HANDLE_VALUE)
                NativeMethods.CloseHandle(h);
        }

        private static void SetDisposition(IntPtr h, bool delete)
        {
            var disp = new FILE_DISPOSITION_INFO { DeleteFile = delete };
            int size = Marshal.SizeOf(typeof(FILE_DISPOSITION_INFO));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(disp, buf, false);
                if (!NativeMethods.SetFileInformationByHandle(h,
                        NativeMethods.FileDispositionInfo, buf, (uint)size))
                    throw WinErr(Marshal.GetLastWin32Error(),
                        delete ? "无法设置删除待决标记" : "无法清除删除待决标记");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static void SetEof(IntPtr h, ulong size)
        {
            var eof = new FILE_END_OF_FILE_INFO { EndOfFile = size };
            int sz = Marshal.SizeOf(typeof(FILE_END_OF_FILE_INFO));
            IntPtr buf = Marshal.AllocHGlobal(sz);
            try
            {
                Marshal.StructureToPtr(eof, buf, false);
                if (!NativeMethods.SetFileInformationByHandle(h,
                        NativeMethods.FileEndOfFileInfo, buf, (uint)sz))
                    throw WinErr(Marshal.GetLastWin32Error(), "无法设置目标文件大小");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Query allocated (non-hole) ranges. Returns false when the file system
        /// does not support FSCTL_QUERY_ALLOCATED_RANGES (the Win32 support
        /// matrix predates modern ReFS; availability is probed at runtime).
        /// </summary>
        public static bool TryQueryAllocatedRanges(string path, out List<long[]> ranges)
        {
            ranges = null;
            IntPtr h = NativeMethods.CreateFileW(NormalizePath(path), NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == NativeMethods.INVALID_HANDLE_VALUE) return false;
            IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
            try
            {
                ulong size;
                if (!NativeMethods.GetFileSizeEx(h, out size)) return false;
                var list = new List<long[]>();
                long offset = 0;
                inBuf = Marshal.AllocHGlobal(16);
                const int OUT_CAP = 64 * 1024;
                outBuf = Marshal.AllocHGlobal(OUT_CAP);
                while (offset < (long)size)
                {
                    Marshal.WriteInt64(inBuf, 0, offset);
                    Marshal.WriteInt64(inBuf, 8, (long)size - offset);
                    uint returned;
                    if (!NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_QUERY_ALLOCATED_RANGES,
                            inBuf, 16, outBuf, OUT_CAP, out returned, IntPtr.Zero))
                    {
                        if (Marshal.GetLastWin32Error() == 38) break; // ERROR_HANDLE_EOF
                        return false;
                    }
                    if (returned == 0) break;
                    int count = (int)(returned / 16);
                    for (int i = 0; i < count; i++)
                    {
                        long ro = Marshal.ReadInt64(outBuf, i * 16);
                        long rl = Marshal.ReadInt64(outBuf, i * 16 + 8);
                        list.Add(new[] { ro, rl });
                        offset = ro + rl;
                    }
                    if (returned < OUT_CAP) break;
                }
                ranges = list;
                return true;
            }
            catch { return false; }
            finally
            {
                if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
                if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
                NativeMethods.CloseHandle(h);
            }
        }

        /// <summary>
        /// Probe the volume (and file, when it exists) behind a path. For a
        /// non-existing destination the parent directory is probed, which also
        /// reveals the integrity setting a new file would inherit.
        /// </summary>
        public static bool TryProbe(string path, out VolumeInfo info)
        {
            info = null;
            IntPtr h = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            try
            {
                string p = NormalizePath(path);
                bool pathExists = false;
                bool isDir = false;
                uint attr = NativeMethods.GetFileAttributesW(p);
                string target = p;
                if (attr == 0xFFFFFFFF)
                {
                    target = System.IO.Path.GetDirectoryName(p);
                    if (string.IsNullOrEmpty(target)) target = ".";
                    isDir = true;
                }
                else
                {
                    pathExists = true;
                    isDir = (attr & 0x10) != 0; // FILE_ATTRIBUTE_DIRECTORY
                }

                h = NativeMethods.CreateFileW(target, NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                    isDir ? NativeMethods.FILE_FLAG_BACKUP_SEMANTICS : 0, IntPtr.Zero);
                if (h == NativeMethods.INVALID_HANDLE_VALUE && !isDir)
                    h = NativeMethods.CreateFileW(target, NativeMethods.GENERIC_READ,
                        NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                        IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                        NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (h == NativeMethods.INVALID_HANDLE_VALUE) return false;

                var vi = new VolumeInfo();
                vi.PathExists = pathExists;
                var sb = new System.Text.StringBuilder(64);
                uint serial, flags;
                if (!NativeMethods.GetVolumeInformationByHandleW(h, IntPtr.Zero, 0,
                        out serial, IntPtr.Zero, out flags, sb, (uint)sb.Capacity))
                    return false;
                vi.Serial = serial;
                vi.FileSystem = sb.ToString();
                vi.SupportsBlockClone = (flags & NativeMethods.FILE_SUPPORTS_BLOCK_REFCOUNTING) != 0;

                int sizeGi = Marshal.SizeOf(typeof(FSCTL_GET_INTEGRITY_INFORMATION_BUFFER));
                buf = Marshal.AllocHGlobal(sizeGi);
                uint dummy;
                if (NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_GET_INTEGRITY_INFORMATION,
                        IntPtr.Zero, 0, buf, (uint)sizeGi, out dummy, IntPtr.Zero))
                {
                    var gi = (FSCTL_GET_INTEGRITY_INFORMATION_BUFFER)Marshal.PtrToStructure(
                        buf, typeof(FSCTL_GET_INTEGRITY_INFORMATION_BUFFER));
                    vi.ClusterSize = gi.ClusterSizeInBytes;
                    vi.IntegrityEnabled = gi.ChecksumAlgorithm != 0;
                    vi.ChecksumEnforcementOff = (gi.Flags & 1) != 0;
                }
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;

                uint hattr = AttributesOfHandle(h);
                if (pathExists && hattr != 0xFFFFFFFF)
                {
                    isDir = (hattr & 0x10) != 0;
                    vi.IsDirectory = isDir;
                    vi.IsSparse = !isDir && (hattr & NativeMethods.FILE_ATTRIBUTE_SPARSE_FILE) != 0;
                }
                else if (pathExists)
                {
                    vi.IsDirectory = isDir;
                    vi.IsSparse = !isDir && attr != 0xFFFFFFFF &&
                        (attr & NativeMethods.FILE_ATTRIBUTE_SPARSE_FILE) != 0;
                }
                info = vi;
                return true;
            }
            catch { return false; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                CloseIfValid(h);
            }
        }

        private static uint AttributesOfHandle(IntPtr h)
        {
            int sz = Marshal.SizeOf(typeof(FILE_BASIC_INFO));
            IntPtr b = Marshal.AllocHGlobal(sz);
            try
            {
                if (!NativeMethods.GetFileInformationByHandleEx(h, NativeMethods.FileBasicInfo, b, (uint)sz))
                    return 0xFFFFFFFF;
                var info = (FILE_BASIC_INFO)Marshal.PtrToStructure(b, typeof(FILE_BASIC_INFO));
                return info.FileAttributes;
            }
            catch { return 0xFFFFFFFF; }
            finally { Marshal.FreeHGlobal(b); }
        }

        private sealed class AlternateStream
        {
            public readonly string Name;
            public AlternateStream(string name) { Name = name; }
        }

        private List<AlternateStream> EnumerateAlternateStreams(string src)
        {
            var list = new List<AlternateStream>();
            var data = new WIN32_FIND_STREAM_DATA();
            IntPtr h = NativeMethods.FindFirstStreamW(src, 0, ref data, 0);
            if (h == NativeMethods.INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 38 && err != 1)
                    _log("警告：无法枚举备用数据流（Win32 " + err + "），源含 ADS 时不会被复制。");
                return list;
            }
            try
            {
                do
                {
                    string name = data.StreamName;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.StartsWith(":", StringComparison.Ordinal)) name = ":" + name;
                    if (string.Equals(name, "::$DATA", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, ":$DATA", StringComparison.OrdinalIgnoreCase))
                        continue;
                    list.Add(new AlternateStream(name));
                }
                while (NativeMethods.FindNextStreamW(h, ref data));
            }
            finally { NativeMethods.FindClose(h); }
            if (list.Count > 0) _log("发现备用数据流 " + list.Count + " 个。");
            return list;
        }

        private void CopyAlternateStreams(string src, string dst,
            List<AlternateStream> streams, CancellationToken cancellationToken)
        {
            if (streams == null || streams.Count == 0) return;
            int copied = 0;
            long bytes = 0;
            foreach (var stream in streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr hi = IntPtr.Zero;
                IntPtr ho = IntPtr.Zero;
                try
                {
                    string srcStream = src + stream.Name;
                    string dstStream = dst + stream.Name;
                    hi = NativeMethods.CreateFileW(srcStream, NativeMethods.GENERIC_READ,
                        NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                        IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                    if (hi == NativeMethods.INVALID_HANDLE_VALUE)
                        throw WinErr(Marshal.GetLastWin32Error(), "无法打开源备用数据流：" + stream.Name);
                    ho = NativeMethods.CreateFileW(dstStream,
                        NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                        NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                        IntPtr.Zero, NativeMethods.CREATE_NEW, 0, IntPtr.Zero);
                    if (ho == NativeMethods.INVALID_HANDLE_VALUE)
                        throw WinErr(Marshal.GetLastWin32Error(), "无法创建目标备用数据流：" + stream.Name);

                    ulong size;
                    if (!NativeMethods.GetFileSizeEx(hi, out size))
                        throw WinErr(Marshal.GetLastWin32Error(), "无法获取备用数据流大小：" + stream.Name);
                    long np;
                    if (!NativeMethods.SetFilePointerEx(hi, 0, out np, NativeMethods.FILE_BEGIN) ||
                        !NativeMethods.SetFilePointerEx(ho, 0, out np, NativeMethods.FILE_BEGIN))
                        throw WinErr(Marshal.GetLastWin32Error(), "无法定位备用数据流：" + stream.Name);

                    var buf = new byte[64 * 1024];
                    ulong done = 0;
                    while (done < size)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        uint want = (uint)Math.Min((ulong)buf.Length, size - done);
                        uint n;
                        if (!NativeMethods.ReadFile(hi, buf, want, out n, IntPtr.Zero))
                            throw WinErr(Marshal.GetLastWin32Error(), "无法读取源备用数据流：" + stream.Name);
                        if (n == 0)
                            throw new CloneException("源备用数据流在克隆期间被截断：" + stream.Name);
                        uint w;
                        if (!NativeMethods.WriteFile(ho, buf, n, out w, IntPtr.Zero) || w != n)
                            throw WinErr(Marshal.GetLastWin32Error(), "无法写入目标备用数据流：" + stream.Name);
                        done += n;
                    }
                    copied++;
                    bytes += (long)size;
                }
                finally
                {
                    CloseIfValid(hi);
                    CloseIfValid(ho);
                }
            }
            _log(string.Format("已复制备用数据流 {0} 个（{1} 字节，普通 I/O）。", copied, bytes));
        }

        /// <summary>Accept quoted paths, forward slashes, and &gt;259-char paths.</summary>
        internal static string NormalizePath(string p)
        {
            if (p == null) return "";
            p = p.Trim().Trim('"').Replace('/', '\\');
            if (p.Length > 259 && !p.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                if (p.StartsWith(@"\\", StringComparison.Ordinal))
                    p = @"\\?\UNC\" + p.Substring(2);
                else if (System.IO.Path.IsPathRooted(p))
                    p = @"\\?\" + p;
                else
                    p = @"\\?\" + System.IO.Path.Combine(Environment.CurrentDirectory, p);
            }
            return p;
        }

        private static Win32ExceptionEx WinErr(int code, string what)
        {
            return new Win32ExceptionEx(code, what);
        }
    }

    /// <summary>Exception carrying the raw Win32 error code with a formatted,
    /// human-readable message (system text + ReFS-specific hint).</summary>
    public sealed class Win32ExceptionEx : Exception
    {
        public int NativeErrorCode { get; private set; }

        public Win32ExceptionEx(int code, string what)
            : base(Format(code, what))
        {
            NativeErrorCode = code;
        }

        private static string Format(int code, string what)
        {
            string sys = new System.ComponentModel.Win32Exception(code).Message;
            string hint = Hint(code);
            string msg = string.Format("{0}（Win32 {1} / 0x{1:X}：{2}）", what, code, sys);
            if (hint != null) msg += "\n提示：" + hint;
            return msg;
        }

        private static string Hint(int code)
        {
            switch (code)
            {
                case 1: return "该文件系统不支持此 FSCTL（例如旧版 ReFS 不支持已分配区间查询）。";
                case 5: return "权限不足，或文件被其他进程以不兼容的共享模式占用。";
                case 19: return "卷处于只读状态。";
                case 32: return "文件被其他进程占用（共享冲突），请关闭占用程序后重试。";
                case 33: return "源或目标区间存在字节范围锁冲突（LockFile），请解除锁定后重试。";
                case 50: return "常见原因：卷不是 ReFS 或 ReFS 版本过旧；源与目标 Integrity Streams 设置不一致；源稀疏而目标不稀疏；克隆区间超出源文件末尾。";
                case 87: return "常见原因：偏移/长度未按簇边界对齐；源与目标不在同一卷。";
                case 112: return "卷剩余空间不足。";
                case 347: return "物理块引用数已达 ReFS 上限（8175）。请更换基线文件或减少同一源文件的克隆数量。";
                default: return null;
            }
        }
    }
}
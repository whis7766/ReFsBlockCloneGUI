// ReFS block-clone engine.
//
// Corrected against Microsoft primary sources (see docs/DESIGN.md):
//   * [MS-FSA] 2.1.5.10.4: the source range is validated against Source.Size
//     (logical EOF), NOT the rounded-up allocation size. Therefore a non
//     cluster-aligned file is cloned as an aligned PREFIX via
//     FSCTL_DUPLICATE_EXTENTS_TO_FILE plus a normal read/write of the trailing
//     partial cluster. The old "round everything up, clone, shrink back"
//     approach is rejected by ReFS with ERROR_NOT_SUPPORTED.
//   * Block cloning restrictions (Win32 "Block Cloning" / ReFS "Block cloning"):
//     cluster-aligned offsets/lengths, < 4 GB per call, same ReFS volume,
//     identical Integrity Streams settings, sparse source => sparse target,
//     destination region must not extend past EOF (SetEndOfFile first),
//     max 8175 references per physical region (ERROR_BLOCK_TOO_MANY_REFERENCES).
//   * Sparse attribute is MIRRORED from the source (the old code marked every
//     target sparse, changing the attributes of dense-file clones).
//   * Delete-pending is a process-level cleanup aid, set immediately after
//     CREATE_NEW. It is NOT required for cloning itself and does not survive
//     an OS crash; a best-effort DeleteFileW fallback covers the tiny window
//     before the disposition is set.
using System;
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

    internal static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint DELETE = 0x00010000;
        public const uint FILE_SHARE_READ = 0x00000001;
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
    }

    /// <summary>
    /// Block-clone engine. FSCTL_DUPLICATE_EXTENTS_TO_FILE remaps VCN-&gt;LCN
    /// references (refcounts) without copying data bytes; isolation afterwards
    /// is preserved by ReFS allocate-on-write. Only the trailing partial
    /// cluster of a non-aligned file is copied as ordinary data.
    /// </summary>
    public sealed class RefsBlockCloner
    {
        private readonly Action<string> _log;

        public RefsBlockCloner(Action<string> log) { _log = log ?? (s => { }); }

        /// <summary>
        /// Block-clone <paramref name="inFile"/> to <paramref name="outFile"/>.
        /// Both must live on the same ReFS volume; the destination must not exist.
        /// On any failure the partial destination is removed (delete-pending).
        /// </summary>
        public void Clone(string inFile, string outFile,
            IProgress<CloneProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string src = NormalizePath(inFile);
            string dst = NormalizePath(outFile);
            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                throw new CloneException("源文件与目标文件不能相同。");

            IntPtr hIn = IntPtr.Zero;
            IntPtr hOut = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            bool created = false;
            bool dispositionSet = false;
            try
            {
                // ---- source open + volume capability ----
                _log("打开源文件...");
                hIn = NativeMethods.CreateFileW(src, NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ, IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                if (hIn == NativeMethods.INVALID_HANDLE_VALUE)
                    throw WinErr(Marshal.GetLastWin32Error(), "无法打开源文件：" + inFile);

                uint srcFlags, srcSerial;
                if (!NativeMethods.GetVolumeInformationByHandleW(hIn, IntPtr.Zero, 0,
                        out srcSerial, IntPtr.Zero, out srcFlags, IntPtr.Zero, 0))
                    throw WinErr(Marshal.GetLastWin32Error(), "无法获取源卷信息");
                if ((srcFlags & NativeMethods.FILE_SUPPORTS_BLOCK_REFCOUNTING) == 0)
                    throw new CloneException("源卷不支持块克隆（需要 Windows Server 2016+ 格式化的 ReFS 卷）。");
                _log(string.Format("源卷支持块克隆（卷序列号 0x{0:X8}）", srcSerial));

                ulong srcSize;
                if (!NativeMethods.GetFileSizeEx(hIn, out srcSize))
                    throw WinErr(Marshal.GetLastWin32Error(), "无法获取源文件大小");

                uint srcAttr = NativeMethods.GetFileAttributesW(src);
                bool srcSparse = srcAttr != 0xFFFFFFFF &&
                    (srcAttr & NativeMethods.FILE_ATTRIBUTE_SPARSE_FILE) != 0;

                // ---- source integrity info: cluster size + checksum settings ----
                var gi = new FSCTL_GET_INTEGRITY_INFORMATION_BUFFER();
                int sizeGi = Marshal.SizeOf(typeof(FSCTL_GET_INTEGRITY_INFORMATION_BUFFER));
                buf = Marshal.AllocHGlobal(sizeGi);
                uint dummy;
                if (!NativeMethods.DeviceIoControl(hIn, NativeMethods.FSCTL_GET_INTEGRITY_INFORMATION,
                        IntPtr.Zero, 0, buf, (uint)sizeGi, out dummy, IntPtr.Zero))
                {
                    Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                    throw WinErr(Marshal.GetLastWin32Error(), "无法获取源文件完整性信息");
                }
                gi = (FSCTL_GET_INTEGRITY_INFORMATION_BUFFER)Marshal.PtrToStructure(
                    buf, typeof(FSCTL_GET_INTEGRITY_INFORMATION_BUFFER));
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;

                ulong cluster = gi.ClusterSizeInBytes;
                if (cluster == 0) cluster = 4096;
                ulong prefix = srcSize - (srcSize % cluster); // aligned, cloneable part
                ulong tail = srcSize - prefix;                // partial cluster, ordinary I/O
                _log(string.Format("簇大小 {0} 字节，源大小 {1} 字节：块克隆前缀 {2} 字节 + 普通复制尾部 {3} 字节",
                    cluster, srcSize, prefix, tail));

                // ---- create destination; arm cleanup IMMEDIATELY ----
                _log("创建目标文件...");
                hOut = NativeMethods.CreateFileW(dst,
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE | NativeMethods.DELETE,
                    0, IntPtr.Zero, NativeMethods.CREATE_NEW, 0, IntPtr.Zero);
                if (hOut == NativeMethods.INVALID_HANDLE_VALUE)
                    throw WinErr(Marshal.GetLastWin32Error(), "无法创建目标文件：" + outFile);
                created = true;

                // Delete-pending: when this handle (the last one) closes on any
                // failure path, ReFS/NTFS removes the partial file. This is a
                // cleanup aid only; it has no effect on clone capability.
                SetDisposition(hOut, true);
                dispositionSet = true;

                // ---- destination volume must match source ----
                uint dstFlags, dstSerial;
                if (!NativeMethods.GetVolumeInformationByHandleW(hOut, IntPtr.Zero, 0,
                        out dstSerial, IntPtr.Zero, out dstFlags, IntPtr.Zero, 0))
                    throw WinErr(Marshal.GetLastWin32Error(), "无法获取目标卷信息");
                if ((dstFlags & NativeMethods.FILE_SUPPORTS_BLOCK_REFCOUNTING) == 0)
                    throw new CloneException("目标卷不支持块克隆，目标须与源位于同一 ReFS 卷。");
                if (srcSerial != dstSerial)
                    throw new CloneException("源与目标必须位于同一 ReFS 卷（卷序列号不匹配）。");

                // ---- mirror sparse attribute (sparse source REQUIRES sparse target;
                //      dense source must stay dense for attribute fidelity) ----
                if (srcSparse)
                {
                    if (!NativeMethods.DeviceIoControl(hOut, NativeMethods.FSCTL_SET_SPARSE,
                            IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero))
                        throw WinErr(Marshal.GetLastWin32Error(), "无法将目标标记为稀疏");
                    _log("源为稀疏文件，目标已标记稀疏。");
                }

                // ---- mirror integrity streams (must match; target may have
                //      inherited a different setting from its parent directory).
                //      Done while the file is still empty. ----
                var si = new FSCTL_SET_INTEGRITY_INFORMATION_BUFFER
                {
                    ChecksumAlgorithm = gi.ChecksumAlgorithm,
                    Reserved = 0,
                    Flags = gi.Flags
                };
                int sizeSi = Marshal.SizeOf(typeof(FSCTL_SET_INTEGRITY_INFORMATION_BUFFER));
                buf = Marshal.AllocHGlobal(sizeSi);
                Marshal.StructureToPtr(si, buf, false);
                bool okSi = NativeMethods.DeviceIoControl(hOut,
                    NativeMethods.FSCTL_SET_INTEGRITY_INFORMATION, buf, (uint)sizeSi,
                    IntPtr.Zero, 0, out dummy, IntPtr.Zero);
                int siErr = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                if (!okSi)
                    throw WinErr(siErr, "无法设置目标文件完整性信息");
                _log(gi.ChecksumAlgorithm != 0
                    ? "已复制源完整性 streams 设置（启用）。"
                    : "已复制源完整性 streams 设置（关闭）。");

                // ---- destination EOF = exact source size. The clone prefix ends
                //      at or before EOF; the tail write stays inside EOF. No
                //      shrink step is needed. ----
                SetEof(hOut, srcSize);

                // ---- block-clone the aligned prefix in < 4 GB chunks ----
                var dup = new DUPLICATE_EXTENTS_DATA { FileHandle = hIn };
                int sizeDup = Marshal.SizeOf(typeof(DUPLICATE_EXTENTS_DATA));
                buf = Marshal.AllocHGlobal(sizeDup);
                const ulong CHUNK = 100UL * 1024 * 1024; // cluster multiple, far below 4 GB
                ulong offset = 0;
                while (offset < prefix)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ulong bc = Math.Min(CHUNK, prefix - offset);
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

                // ---- ordinary copy of the trailing partial cluster ----
                if (tail > 0)
                {
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
                        if (!NativeMethods.ReadFile(hIn, tmp, want, out n, IntPtr.Zero) || n == 0)
                            throw WinErr(Marshal.GetLastWin32Error(), "无法读取源文件尾部");
                        Array.Copy(tmp, 0, data, (int)got, (int)n);
                        got += n;
                    }

                    bool allZero = true;
                    for (int i = 0; i < data.Length; i++) if (data[i] != 0) { allZero = false; break; }
                    if (srcSparse && allZero)
                    {
                        _log("尾部为空洞，保持稀疏，不写入。");
                    }
                    else
                    {
                        if (!NativeMethods.SetFilePointerEx(hOut, (long)prefix, out np, NativeMethods.FILE_BEGIN))
                            throw WinErr(Marshal.GetLastWin32Error(), "无法定位目标文件尾部");
                        uint wr;
                        if (!NativeMethods.WriteFile(hOut, data, (uint)tail, out wr, IntPtr.Zero) || wr != tail)
                            throw WinErr(Marshal.GetLastWin32Error(), "无法写入目标文件尾部");
                        _log(string.Format("尾部 {0} 字节已普通复制。", tail));
                    }
                }
                if (progress != null) progress.Report(new CloneProgress((long)srcSize, (long)srcSize));

                // ---- success: disarm cleanup so the clone survives ----
                SetDisposition(hOut, false);
                dispositionSet = false;
                _log("克隆完成。");
            }
            catch
            {
                // Close the tiny window between CREATE_NEW and delete-pending:
                // if the disposition was never armed, remove the file explicitly.
                if (created && !dispositionSet)
                {
                    if (hOut != IntPtr.Zero && hOut != NativeMethods.INVALID_HANDLE_VALUE)
                        NativeMethods.CloseHandle(hOut);
                    hOut = IntPtr.Zero;
                    NativeMethods.DeleteFileW(dst);
                }
                throw;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (hIn != IntPtr.Zero && hIn != NativeMethods.INVALID_HANDLE_VALUE)
                    NativeMethods.CloseHandle(hIn);
                if (hOut != IntPtr.Zero && hOut != NativeMethods.INVALID_HANDLE_VALUE)
                    NativeMethods.CloseHandle(hOut); // delete-pending still armed -> partial removed
            }
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
                bool isDir = false;
                uint attr = NativeMethods.GetFileAttributesW(p);
                string target = p;
                if (attr == 0xFFFFFFFF)
                {
                    target = System.IO.Path.GetDirectoryName(p);
                    if (string.IsNullOrEmpty(target)) return false;
                    isDir = true;
                }
                else if ((attr & 0x10) != 0) isDir = true; // FILE_ATTRIBUTE_DIRECTORY

                h = NativeMethods.CreateFileW(target, NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ | 0x2 | 0x4, IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    isDir ? NativeMethods.FILE_FLAG_BACKUP_SEMANTICS : 0, IntPtr.Zero);
                if (h == NativeMethods.INVALID_HANDLE_VALUE) return false;

                var vi = new VolumeInfo();
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

                if (!isDir)
                {
                    uint fa = NativeMethods.GetFileAttributesW(p);
                    vi.IsSparse = fa != 0xFFFFFFFF && (fa & NativeMethods.FILE_ATTRIBUTE_SPARSE_FILE) != 0;
                }
                info = vi;
                return true;
            }
            catch { return false; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (h != IntPtr.Zero && h != NativeMethods.INVALID_HANDLE_VALUE)
                    NativeMethods.CloseHandle(h);
            }
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
                case 5: return "权限不足，或文件被其他进程以不兼容的共享模式占用。";
                case 19: return "卷处于只读状态。";
                case 32: return "文件被其他进程占用（共享冲突），请关闭占用程序后重试。";
                case 33: return "源或目标区间存在字节范围锁冲突（LockFile），请解除锁定后重试。";
                case 50: return "常见原因：卷不是 ReFS 或 ReFS 版本过旧；源与目标 Integrity Streams 设置不一致；源稀疏而目标不稀疏；克隆区间超出源文件末尾。";
                case 87: return "常见原因：偏移/长度未按簇边界对齐；源与目标不在同一卷。";
                case 112: return "卷剩余空间不足。";
                case 347: return "物理块引用数已达 ReFS 上限（8175）。请先对部分既有克隆做普通物理复制以降低共享度，再重试。";
                default: return null;
            }
        }
    }
}
// Console harness that compiles the SAME ReFsBlockCloner.cs used by the GUI and
// verifies it end-to-end on real ReFS volumes.
// Usage: ReFsCloneTest.exe <ReFS-drive e.g. R> [second-ReFS-drive e.g. S] [quick|full]
//   R : 4K-cluster ReFS volume   S : 64K-cluster ReFS volume (optional but recommended)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Threading;
using ReFsBlockClone;

internal static class ReFsCloneTest
{
    private static int _fail = 0;
    private static int _pass = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string name);
    private const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x200;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string n, uint a, uint s, IntPtr c, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr i, uint isz, IntPtr o, uint osz, out uint r, IntPtr v);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEndOfFile(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(IntPtr h, long d, out long n, uint m);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr h, byte[] b, uint n, out uint w, IntPtr v);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(IntPtr h, out ulong sz);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(IntPtr h, byte[] b, uint n, out uint r, IntPtr v);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileAttributesW(string name, uint attrs);

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint CREATE_ALWAYS = 2, OPEN_EXISTING = 3, FILE_BEGIN = 0;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FSCTL_SET_SPARSE = 0x000900C4;
    private const uint FSCTL_GET_INTEGRITY_INFORMATION = 0x0009027C;
    private const uint FSCTL_SET_INTEGRITY_INFORMATION = 0x0009C280;
    private const uint FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x00098344;

    private static void Check(string name, bool cond)
    {
        Console.WriteLine((cond ? "  [PASS] " : "  [FAIL] ") + name);
        if (cond) _pass++; else _fail++;
    }

    private static void Info(string msg) { Console.WriteLine("  [INFO] " + msg); }

    private static void CreateDense(string path, long size, int seed)
    {
        var rng = new Random(seed);
        var buf = new byte[1024 * 1024];
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            long rem = size;
            while (rem > 0)
            {
                int n = (int)Math.Min(rem, buf.Length);
                rng.NextBytes(buf);
                fs.Write(buf, 0, n);
                rem -= n;
            }
        }
    }

    private static void DoCreateSparse(string path, long logical, long[][] regions)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, CREATE_ALWAYS, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new IOException("create sparse failed: " + Marshal.GetLastWin32Error());
        uint dummy; DeviceIoControl(h, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out dummy, IntPtr.Zero);
        long np; SetFilePointerEx(h, logical, out np, FILE_BEGIN); SetEndOfFile(h);
        var rng = new Random(99);
        foreach (var r in regions)
        {
            var buf = new byte[r[1]];
            rng.NextBytes(buf);
            uint w;
            SetFilePointerEx(h, r[0], out np, FILE_BEGIN);
            WriteFile(h, buf, (uint)buf.Length, out w, IntPtr.Zero);
        }
        CloseHandle(h);
    }

    private static bool IsSparse(string p)
    {
        return (GetFileAttributesW(p) & FILE_ATTRIBUTE_SPARSE_FILE) != 0;
    }

    // null = FSCTL_QUERY_ALLOCATED_RANGES unsupported on this file system.
    private static bool? HoleMapEqual(string a, string b)
    {
        System.Collections.Generic.List<long[]> ra, rb;
        bool sa = RefsBlockCloner.TryQueryAllocatedRanges(a, out ra);
        bool sb = RefsBlockCloner.TryQueryAllocatedRanges(b, out rb);
        if (!sa || !sb) return null;
        if (ra.Count != rb.Count) return false;
        for (int i = 0; i < ra.Count; i++)
            if (ra[i][0] != rb[i][0] || ra[i][1] != rb[i][1]) return false;
        return true;
    }

    private static void CheckHoles(string label, string a, string b)
    {
        bool? eq = HoleMapEqual(a, b);
        if (eq == null)
            Info(label + ": QUERY_ALLOCATED_RANGES unsupported here; hole-map check skipped");
        else
        {
            if (eq != true)
            {
                Console.WriteLine("      map a: " + DumpRanges(a));
                Console.WriteLine("      map b: " + DumpRanges(b));
            }
            Check(label + " hole map identical", eq == true);
        }
    }

    private static string DumpRanges(string p)
    {
        List<long[]> r;
        if (!RefsBlockCloner.TryQueryAllocatedRanges(p, out r)) return "<unsupported>";
        return string.Join(",", r.Select(x => x[0] + "+" + x[1]));
    }

    // Ground-truth allocated map derived by scanning bytes (4K units), independent
    // of any file system's QUERY_ALLOCATED_RANGES reporting quirks.
    private static List<long[]> ScanRanges(string p)
    {
        var res = new List<long[]>();
        using (var f = File.OpenRead(p))
        {
            var buf = new byte[1 << 20];
            long off = 0, runStart = -1; int n;
            while ((n = f.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i += 4096)
                {
                    int end = Math.Min(i + 4096, n);
                    bool zero = true;
                    for (int k = i; k < end; k++) if (buf[k] != 0) { zero = false; break; }
                    long u = off + i;
                    if (!zero) { if (runStart < 0) runStart = u; }
                    else if (runStart >= 0) { res.Add(new long[] { runStart, u - runStart }); runStart = -1; }
                }
                off += n;
            }
            if (runStart >= 0) res.Add(new long[] { runStart, off - runStart });
        }
        return res;
    }

    private static bool RangesEqual(List<long[]> a, List<long[]> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i][0] != b[i][0] || a[i][1] != b[i][1]) return false;
        return true;
    }

    // Best-effort variant: report mismatch as INFO (used where the destination
    // file system may not support hole punching, e.g. ReFS SET_ZERO_DATA).
    private static void InfoHoles(string label, string a, string b)
    {
        bool? eq = HoleMapEqual(a, b);
        if (eq == null)
            Info(label + ": QUERY_ALLOCATED_RANGES unsupported here; hole-map check skipped");
        else if (eq == true)
            Check(label + " hole map identical", true);
        else
            Info(label + ": hole map differs (destination FS may not support punching); content verified separately");
    }

    private static void CheckMetadata(string src, string dst)
    {
        var si = new FileInfo(src); var di = new FileInfo(dst);
        Check("last-write time mirrored", si.LastWriteTimeUtc == di.LastWriteTimeUtc);
        Check("creation time mirrored", si.CreationTimeUtc == di.CreationTimeUtc);
        // Compare owner/group and EFFECTIVE access entries (inheritance control
        // bits are destination-dir policy, not part of the mirrored ACL).
        var acS = File.GetAccessControl(src); var acD = File.GetAccessControl(dst);
        string ownS = acS.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)).Value;
        string ownD = acD.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)).Value;
        string grpS = acS.GetGroup(typeof(System.Security.Principal.SecurityIdentifier)).Value;
        string grpD = acD.GetGroup(typeof(System.Security.Principal.SecurityIdentifier)).Value;
        Check("owner mirrored", ownS == ownD);
        Check("group mirrored", grpS == grpD);
        string eaS = EffectiveAcl(acS), eaD = EffectiveAcl(acD);
        if (eaS != eaD)
        {
            Console.WriteLine("      eff src: " + eaS);
            Console.WriteLine("      eff dst: " + eaD);
        }
        Check("effective DACL mirrored", eaS == eaD);
    }

    private static string EffectiveAcl(FileSecurity fs)
    {
        // Aggregate granted/denied rights per trustee: Windows may merge or split
        // same-trustee ACEs, so compare the UNION of rights, not ACE granularity.
        var map = new Dictionary<string, int>();
        foreach (FileSystemAccessRule r in fs.GetAccessRules(true, true,
            typeof(System.Security.Principal.SecurityIdentifier)))
        {
            string key = r.IdentityReference.Value + "|" + r.AccessControlType;
            int cur; map.TryGetValue(key, out cur);
            map[key] = cur | (int)r.FileSystemRights;
        }
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        var sb = new System.Text.StringBuilder();
        foreach (var k in keys) sb.Append(k).Append('=').Append(map[k]).Append(';');
        return sb.ToString();
    }

    private static long FirstDiff(string a, string b)
    {
        using (var fa = File.OpenRead(a)) using (var fb = File.OpenRead(b))
        {
            var ba = new byte[1 << 16]; var bb = new byte[1 << 16];
            while (true)
            {
                int na = fa.Read(ba, 0, ba.Length); int nb = fb.Read(bb, 0, bb.Length);
                if (na != nb) return off;
                if (na == 0) return -1;
                for (int i = 0; i < na; i++) if (ba[i] != bb[i]) return off + i;
                off += na;
            }
        }
    }

    private static void SetIntegrity(string path, bool enable, bool isDir)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0x1 | 0x2 | 0x4, IntPtr.Zero,
            OPEN_EXISTING, isDir ? FILE_FLAG_BACKUP_SEMANTICS : 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new IOException("open for integrity failed: " + Marshal.GetLastWin32Error());
        var buf = Marshal.AllocHGlobal(8);
        Marshal.WriteInt16(buf, 0, (short)(enable ? 2 : 0)); // CHECKSUM_TYPE_CRC64 / NONE
        Marshal.WriteInt16(buf, 2, 0);
        Marshal.WriteInt32(buf, 4, 0);
        uint dummy;
        bool ok = DeviceIoControl(h, FSCTL_SET_INTEGRITY_INFORMATION, buf, 8, IntPtr.Zero, 0, out dummy, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        Marshal.FreeHGlobal(buf);
        CloseHandle(h);
        if (!ok) throw new IOException("set integrity failed: " + err);
    }

    private static bool IntegrityEnabled(string path)
    {
        var h = CreateFileW(path, GENERIC_READ, 0x1 | 0x2 | 0x4, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return false;
        var buf = Marshal.AllocHGlobal(16);
        uint dummy;
        bool ok = DeviceIoControl(h, FSCTL_GET_INTEGRITY_INFORMATION, IntPtr.Zero, 0, buf, 16, out dummy, IntPtr.Zero);
        ushort algo = ok ? (ushort)Marshal.ReadInt16(buf, 0) : (ushort)0;
        Marshal.FreeHGlobal(buf);
        CloseHandle(h);
        return algo != 0;
    }

    private static string Sha(string p)
    {
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(p))
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
    }

    private static bool CloneOk(string src, string dst, out string err)
    {
        err = "";
        try { new RefsBlockCloner(s => Console.WriteLine("      | " + s)).Clone(src, dst); return true; }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    private static bool LenOk(string p, long expect, string label)
    {
        bool exists = File.Exists(p);
        bool ok = exists && new FileInfo(p).Length == expect;
        Check(label + (exists ? " size" : " (missing)"), ok);
        return ok;
    }

    private sealed class InlineProgress : IProgress<CloneProgress>
    {
        private readonly Action<CloneProgress> _a;
        public InlineProgress(Action<CloneProgress> a) { _a = a; }
        public void Report(CloneProgress v) { _a(v); }
    }

    private static int Main(string[] args)
    {
        try
        {
            // Benchmark mode: one clone with an explicit chunk size; CI measures
            // host-side write volume via dynamic-VHDX growth around this call.
            if (args.Length > 0 && args[0] == "bench")
            {
                string bsrc = args[1], bdst = args[2];
                ulong mb = ulong.Parse(args[3]);
                var swb = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    new RefsBlockCloner(s => { }).Clone(bsrc, bdst, null,
                        default(CancellationToken), mb * 1024 * 1024);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("BENCH FAIL chunkMB=" + mb + " " + ex.Message);
                    return 1;
                }
                swb.Stop();
                // Force the destination's metadata/data to stable storage so the
                // CI-side VHDX growth measurement isolates THIS clone's writes
                // (ReFS/NTFS lazy writeback would otherwise leak into the delta).
                try
                {
                    using (var f = new FileStream(bdst, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        f.Flush(true);
                }
                catch { }
                Console.WriteLine("BENCH chunkMB=" + mb + " ms=" + swb.ElapsedMilliseconds);
                return 0;
            }

            string drive = (args.Length > 0 ? args[0] : "R").TrimEnd('\\', ':') + ":\\";
            string drive2 = args.Length > 1 && args[1] != "quick" && args[1] != "full"
                ? args[1].TrimEnd('\\', ':') + ":\\" : null;
            bool quick = args.Length > 0 && args[args.Length - 1] == "quick";
            string dir = Path.Combine(drive, "reftest");
            Directory.CreateDirectory(dir);
            Console.WriteLine("== ReFS block-clone engine verification on " + drive +
                (drive2 != null ? " + " + drive2 : "") + " (quick=" + quick + ") ==");

            long m100 = 100L * 1024 * 1024;

            // [1] dense non-aligned (100MB + 12345): prefix block-clone + tail copy
            Console.WriteLine("\n[1] dense non-aligned 100MB+12345");
            var d1s = Path.Combine(dir, "d1.src"); var d1d = Path.Combine(dir, "d1.dst");
            File.Delete(d1s); File.Delete(d1d);
            CreateDense(d1s, m100 + 12345, 7);
            string e1;
            bool ok1 = CloneOk(d1s, d1d, out e1);
            if (!ok1) Console.WriteLine("      ERR: " + e1);
            Check("clone exit", ok1);
            LenOk(d1d, new FileInfo(d1s).Length, "dst");
            if (File.Exists(d1d)) Check("content equal (SHA256)", Sha(d1s) == Sha(d1d));
            if (File.Exists(d1d)) Check("dense source => dst NOT sparse", !IsSparse(d1d));
            if (File.Exists(d1d)) CheckMetadata(d1s, d1d);
            File.Delete(d1s); File.Delete(d1d);

            // [2] dense 8.231GB non-aligned (full scale)
            if (!quick)
            {
                Console.WriteLine("\n[2] dense 8.231GB+12345 (full scale)");
                var d2s = Path.Combine(dir, "d2.src"); var d2d = Path.Combine(dir, "d2.dst");
                File.Delete(d2s); File.Delete(d2d);
                CreateDense(d2s, 8231L * 1024 * 1024 + 12345, 77);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string e2;
                bool ok2 = CloneOk(d2s, d2d, out e2);
                sw.Stop();
                if (!ok2) Console.WriteLine("      ERR: " + e2);
                Check("clone exit (" + sw.Elapsed.TotalSeconds.ToString("0.000") + "s)", ok2);
                if (ok2)
                {
                    LenOk(d2d, new FileInfo(d2s).Length, "dst");
                    Check("content equal (sampled)", SampleEqual(d2s, d2d));
                }
                File.Delete(d2s); File.Delete(d2d);
            }

            // [3] sparse non-aligned, tail partial cluster HAS data
            Console.WriteLine("\n[3] sparse 100MB+12345, tail partial has data");
            var d3s = Path.Combine(dir, "d3.src"); var d3d = Path.Combine(dir, "d3.dst");
            File.Delete(d3s); File.Delete(d3d);
            DoCreateSparse(d3s, m100 + 12345, new long[][] { new long[] { 0, 1 << 20 }, new long[] { m100 - 4096, 16384 + 57 } });
            string e3;
            bool ok3 = CloneOk(d3s, d3d, out e3);
            if (!ok3) Console.WriteLine("      ERR: " + e3);
            Check("clone exit", ok3);
            LenOk(d3d, new FileInfo(d3s).Length, "dst");
            if (File.Exists(d3d)) Check("dst still SPARSE", IsSparse(d3d));
            if (File.Exists(d3d)) Check("content equal (SHA256)", Sha(d3s) == Sha(d3d));
            if (File.Exists(d3d)) CheckMetadata(d3s, d3d);
            if (File.Exists(d3d)) CheckHoles("sparse tail-data", d3s, d3d);
            File.Delete(d3s); File.Delete(d3d);

            // [4] zero-byte
            Console.WriteLine("\n[4] zero-byte");
            var d4s = Path.Combine(dir, "d4.src"); var d4d = Path.Combine(dir, "d4.dst");
            File.Delete(d4s); File.Delete(d4d);
            File.WriteAllBytes(d4s, new byte[0]);
            string e4;
            bool ok4 = CloneOk(d4s, d4d, out e4);
            if (!ok4) Console.WriteLine("      ERR: " + e4);
            Check("clone exit", ok4);
            LenOk(d4d, 0, "dst");
            File.Delete(d4s); File.Delete(d4d);

            // [5] cross-volume ReFS -> NTFS must be REJECTED, no orphan
            Console.WriteLine("\n[5] cross-volume (ReFS -> NTFS) must fail cleanly");
            var d5s = Path.Combine(dir, "d5.src");
            var d5d = Path.Combine(Path.GetTempPath(), "reftest_cross_" + Guid.NewGuid().ToString("N") + ".dst");
            File.Delete(d5s); File.Delete(d5d);
            CreateDense(d5s, 1024 * 1024, 5);
            string e5;
            bool ok5 = CloneOk(d5s, d5d, out e5);
            Check("clone rejected (exit false)", !ok5);
            if (!ok5) Console.WriteLine("      message: " + e5);
            Check("no orphan on NTFS", !File.Exists(d5d));
            File.Delete(d5s); File.Delete(d5d);

            // [6] sparse with a HOLE in the tail (logical size beyond last write)
            Console.WriteLine("\n[6] sparse 100MB+12345, tail is a hole");
            var d6s = Path.Combine(dir, "d6.src"); var d6d = Path.Combine(dir, "d6.dst");
            File.Delete(d6s); File.Delete(d6d);
            DoCreateSparse(d6s, m100 + 12345, new long[][] { new long[] { 0, 1 << 20 }, new long[] { 50 * 1024 * 1024, 64 * 1024 } });
            string e6;
            bool ok6 = CloneOk(d6s, d6d, out e6);
            if (!ok6) Console.WriteLine("      ERR: " + e6);
            Check("clone exit", ok6);
            LenOk(d6d, new FileInfo(d6s).Length, "dst");
            if (File.Exists(d6d)) Check("dst still SPARSE", IsSparse(d6d));
            if (File.Exists(d6d)) Check("content equal (SHA256)", Sha(d6s) == Sha(d6d));
            if (File.Exists(d6d)) CheckMetadata(d6s, d6d);
            if (File.Exists(d6d)) CheckHoles("sparse tail-hole", d6s, d6d);
            File.Delete(d6s); File.Delete(d6d);

            // [7] sub-cluster dense file (1000 bytes): pure tail copy path
            Console.WriteLine("\n[7] sub-cluster dense 1000 bytes");
            var d7s = Path.Combine(dir, "d7.src"); var d7d = Path.Combine(dir, "d7.dst");
            File.Delete(d7s); File.Delete(d7d);
            CreateDense(d7s, 1000, 11);
            string e7;
            bool ok7 = CloneOk(d7s, d7d, out e7);
            if (!ok7) Console.WriteLine("      ERR: " + e7);
            Check("clone exit", ok7);
            if (File.Exists(d7d)) Check("content equal (SHA256)", Sha(d7s) == Sha(d7d));
            File.Delete(d7s); File.Delete(d7d);

            // [8] 64K-cluster volume: dense + sparse non-aligned
            if (drive2 != null)
            {
                Console.WriteLine("\n[8] 64K-cluster volume " + drive2);
                string dir2 = Path.Combine(drive2, "reftest");
                Directory.CreateDirectory(dir2);
                var d8s = Path.Combine(dir2, "d8.src"); var d8d = Path.Combine(dir2, "d8.dst");
                File.Delete(d8s); File.Delete(d8d);
                CreateDense(d8s, (1 << 20) + 777, 13);
                string e8;
                bool ok8 = CloneOk(d8s, d8d, out e8);
                if (!ok8) Console.WriteLine("      ERR: " + e8);
                Check("64K dense clone exit", ok8);
                if (File.Exists(d8d)) Check("64K dense content equal", Sha(d8s) == Sha(d8d));
                File.Delete(d8s); File.Delete(d8d);

                var d8bs = Path.Combine(dir2, "d8b.src"); var d8bd = Path.Combine(dir2, "d8b.dst");
                File.Delete(d8bs); File.Delete(d8bd);
                DoCreateSparse(d8bs, (1 << 20) + 777, new long[][] { new long[] { 0, 4096 } });
                string e8b;
                bool ok8b = CloneOk(d8bs, d8bd, out e8b);
                if (!ok8b) Console.WriteLine("      ERR: " + e8b);
                Check("64K sparse clone exit", ok8b);
                if (File.Exists(d8bd)) Check("64K sparse content equal", Sha(d8bs) == Sha(d8bd));
                if (File.Exists(d8bd)) Check("64K sparse dst SPARSE", IsSparse(d8bd));
                if (File.Exists(d8bd)) CheckHoles("64K sparse", d8bs, d8bd);
                File.Delete(d8bs); File.Delete(d8bd);

                // [9] cross-ReFS-volume must be REJECTED, no orphan
                Console.WriteLine("\n[9] cross-ReFS-volume (" + drive + " -> " + drive2 + ") must fail cleanly");
                var d9s = Path.Combine(dir, "d9.src"); var d9d = Path.Combine(dir2, "d9.dst");
                File.Delete(d9s); File.Delete(d9d);
                CreateDense(d9s, 1 << 20, 17);
                string e9;
                bool ok9 = CloneOk(d9s, d9d, out e9);
                Check("clone rejected", !ok9);
                if (!ok9) Console.WriteLine("      message: " + e9);
                Check("no orphan on second ReFS volume", !File.Exists(d9d));
                File.Delete(d9s); File.Delete(d9d);
            }

            // [10] integrity streams: enabled source; inherited-from-directory source
            Console.WriteLine("\n[10] integrity streams matching");
            var d10s = Path.Combine(dir, "d10.src"); var d10d = Path.Combine(dir, "d10.dst");
            File.Delete(d10s); File.Delete(d10d);
            CreateDense(d10s, (1 << 20) + 555, 19);
            SetIntegrity(d10s, true, false);
            Check("source integrity enabled", IntegrityEnabled(d10s));
            string e10;
            bool ok10 = CloneOk(d10s, d10d, out e10);
            if (!ok10) Console.WriteLine("      ERR: " + e10);
            Check("integrity-on clone exit", ok10);
            if (File.Exists(d10d)) Check("dst integrity enabled", IntegrityEnabled(d10d));
            if (File.Exists(d10d)) Check("content equal (SHA256)", Sha(d10s) == Sha(d10d));
            if (File.Exists(d10d)) CheckMetadata(d10s, d10d);
            File.Delete(d10s); File.Delete(d10d);

            string dirInt = Path.Combine(dir, "intdir");
            Directory.CreateDirectory(dirInt);
            SetIntegrity(dirInt, true, true);
            var d10bs = Path.Combine(dirInt, "inh.src"); var d10bd = Path.Combine(dir, "inh.dst");
            File.Delete(d10bs); File.Delete(d10bd);
            CreateDense(d10bs, (1 << 20) + 333, 23);
            Check("source inherited integrity", IntegrityEnabled(d10bs));
            string e10b;
            bool ok10b = CloneOk(d10bs, d10bd, out e10b);
            if (!ok10b) Console.WriteLine("      ERR: " + e10b);
            Check("inherited-integrity clone exit", ok10b);
            if (File.Exists(d10bd)) Check("dst integrity ENABLED to match inherited source", IntegrityEnabled(d10bd));
            if (File.Exists(d10bd)) Check("content equal (SHA256)", Sha(d10bs) == Sha(d10bd));
            File.Delete(d10bs); File.Delete(d10bd);
            SetIntegrity(dirInt, false, true);
            Directory.Delete(dirInt, true);

            // reverse inheritance: source OFF, destination directory ON ->
            // engine must switch the target OFF so both settings match.
            string dirInt2 = Path.Combine(dir, "intdir2");
            Directory.CreateDirectory(dirInt2);
            SetIntegrity(dirInt2, true, true);
            var d10cs = Path.Combine(dir, "off.src"); var d10cd = Path.Combine(dirInt2, "off.dst");
            File.Delete(d10cs); File.Delete(d10cd);
            CreateDense(d10cs, (1 << 20) + 222, 41);
            Check("source integrity off", !IntegrityEnabled(d10cs));
            string e10c;
            bool ok10c = CloneOk(d10cs, d10cd, out e10c);
            if (!ok10c) Console.WriteLine("      ERR: " + e10c);
            Check("off-source into on-dir clone exit", ok10c);
            if (File.Exists(d10cd)) Check("dst integrity DISABLED to match source", !IntegrityEnabled(d10cd));
            if (File.Exists(d10cd)) Check("content equal (SHA256)", Sha(d10cs) == Sha(d10cd));
            File.Delete(d10cs); File.Delete(d10cd);
            SetIntegrity(dirInt2, false, true);
            Directory.Delete(dirInt2, true);

            // [11] cancellation mid-clone must clean the partial target
            Console.WriteLine("\n[11] cancellation cleanup");
            var d11s = Path.Combine(dir, "d11.src"); var d11d = Path.Combine(dir, "d11.dst");
            File.Delete(d11s); File.Delete(d11d);
            CreateDense(d11s, 300L * 1024 * 1024, 29);
            var cts = new CancellationTokenSource();
            bool cancelled = false;
            try
            {
                new RefsBlockCloner(s => { }).Clone(d11s, d11d,
                    new InlineProgress(p => { if (p.BytesDone > 0) cts.Cancel(); }), cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { Console.WriteLine("      unexpected: " + ex.Message); }
            Check("OperationCanceledException raised", cancelled);
            Check("no orphan after cancel", !File.Exists(d11d));
            File.Delete(d11s); File.Delete(d11d);

            // [12] DIAGNOSTIC: raw round-up clone on non-aligned source.
            // [MS-FSA] 2.1.5.10.4 validates the source range against Source.Size,
            // so this is expected to FAIL with ERROR_NOT_SUPPORTED (50). Recorded
            // as evidence; either outcome is reported, not asserted.
            Console.WriteLine("\n[12] diagnostic: raw round-up FSCTL on non-aligned source");
            var d12s = Path.Combine(dir, "d12.src"); var d12d = Path.Combine(dir, "d12.dst");
            File.Delete(d12s); File.Delete(d12d);
            CreateDense(d12s, (1 << 20) + 12345, 31);
            int rawErr = RawRoundUpClone(d12s, d12d);
            Info("raw round-up FSCTL result: Win32 " + rawErr +
                (rawErr == 0 ? " (succeeded)" : " (failed as [MS-FSA] predicts)"));
            File.Delete(d12s); File.Delete(d12d);

            // [13] reference-count cap: clone a 1-cluster source until ReFS refuses
            if (!quick)
            {
                Console.WriteLine("\n[13] reference-count cap (expect ERROR_BLOCK_TOO_MANY_REFERENCES 347)");
                var d13s = Path.Combine(dir, "d13.src");
                File.Delete(d13s);
                CreateDense(d13s, 4096, 37);
                int successes = 0; int capErr = 0; string capDst = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 8200; i++)
                {
                    string dst = Path.Combine(dir, "cap_" + i + ".dst");
                    try
                    {
                        new RefsBlockCloner(s => { }).Clone(d13s, dst);
                        successes++;
                    }
                    catch (Win32ExceptionEx wex)
                    {
                        capErr = wex.NativeErrorCode; capDst = dst;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("      unexpected at " + i + ": " + ex.Message);
                        capErr = -1; capDst = dst;
                        break;
                    }
                }
                sw.Stop();
                Info("successful clones before refusal: " + successes + " in " +
                    sw.Elapsed.TotalSeconds.ToString("0.0") + "s; refusal code " + capErr);
                Check("refusal is ERROR_BLOCK_TOO_MANY_REFERENCES (347)", capErr == 347);
                Check("refusal count within documented cap window [8170,8176]",
                    successes >= 8170 && successes <= 8176);
                Check("no orphan after refusal", capDst == null || !File.Exists(capDst));
                for (int i = 0; i < successes; i++) File.Delete(Path.Combine(dir, "cap_" + i + ".dst"));
                File.Delete(d13s);
            }

            // [14] sparse source reached through a symbolic link: sparse state
            // must be derived from the opened handle, not the link's attributes.
            Console.WriteLine("\n[14] sparse source via symbolic link");
            var d14s = Path.Combine(dir, "d14t.src");
            var d14l = Path.Combine(dir, "d14.link");
            var d14d = Path.Combine(dir, "d14.dst");
            File.Delete(d14l); File.Delete(d14s); File.Delete(d14d);
            DoCreateSparse(d14s, m100 + 12345, new long[][] { new long[] { 0, 1 << 20 } });
            bool linkOk = CreateSymbolicLinkW(d14l, d14s, 0);
            if (!linkOk) linkOk = CreateSymbolicLinkW(d14l, d14s, 2);
            if (!linkOk)
            {
                Info("symbolic link unavailable (Win32 " + Marshal.GetLastWin32Error() + "); case skipped");
            }
            else
            {
                string e14;
                bool ok14 = CloneOk(d14l, d14d, out e14);
                if (!ok14) Console.WriteLine("      ERR: " + e14);
                Check("symlink sparse clone exit", ok14);
                if (File.Exists(d14d)) Check("symlink sparse dst SPARSE", IsSparse(d14d));
                if (File.Exists(d14d)) Check("symlink sparse content equal", Sha(d14s) == Sha(d14d));
                if (File.Exists(d14d)) CheckMetadata(d14s, d14d);
            }
            File.Delete(d14l); File.Delete(d14s); File.Delete(d14d);

            // [15] destination already exists: must be refused and left untouched.
            Console.WriteLine("\n[15] destination exists must be preserved");
            var d15s = Path.Combine(dir, "d15.src"); var d15d = Path.Combine(dir, "d15.dst");
            File.Delete(d15s); File.Delete(d15d);
            CreateDense(d15s, 1 << 20, 47);
            CreateDense(d15d, 12345, 48);
            string hash15 = Sha(d15d);
            string e15;
            bool ok15 = CloneOk(d15s, d15d, out e15);
            Check("existing destination rejected", !ok15);
            if (!ok15) Console.WriteLine("      message: " + e15);
            Check("existing destination unchanged", File.Exists(d15d) && Sha(d15d) == hash15);
            File.Delete(d15s); File.Delete(d15d);

            // [16] same source and destination: refused before any I/O.
            Console.WriteLine("\n[16] source equals destination");
            var d16 = Path.Combine(dir, "d16.src");
            File.Delete(d16);
            CreateDense(d16, 1 << 20, 49);
            string hash16 = Sha(d16);
            string e16;
            bool ok16 = CloneOk(d16, d16, out e16);
            Check("same path rejected", !ok16);
            if (!ok16) Console.WriteLine("      message: " + e16);
            Check("same path source unchanged", File.Exists(d16) && Sha(d16) == hash16);
            File.Delete(d16);

            // [17] read-only source attribute must be mirrored (and not block cleanup).
            Console.WriteLine("\n[17] read-only source attribute");
            var d17s = Path.Combine(dir, "d17.src"); var d17d = Path.Combine(dir, "d17.dst");
            File.Delete(d17s); File.Delete(d17d);
            CreateDense(d17s, (1 << 20) + 7, 51);
            SetFileAttributesW(d17s, 0x1); // FILE_ATTRIBUTE_READONLY
            string e17;
            bool ok17 = CloneOk(d17s, d17d, out e17);
            if (!ok17) Console.WriteLine("      ERR: " + e17);
            Check("read-only clone exit", ok17);
            if (File.Exists(d17d)) Check("dst read-only", (GetFileAttributesW(d17d) & 0x1) != 0);
            if (File.Exists(d17d)) Check("content equal (SHA256)", Sha(d17s) == Sha(d17d));
            if (File.Exists(d17d)) CheckMetadata(d17s, d17d);
            if (File.Exists(d17d)) SetFileAttributesW(d17d, 0x80); // NORMAL for delete
            SetFileAttributesW(d17s, 0x80);
            File.Delete(d17s); File.Delete(d17d);

            // [18] alternate data streams: ReFS ADS <=128K, copied with ordinary I/O.
            Console.WriteLine("\n[18] alternate data streams");
            var d18s = Path.Combine(dir, "d18.src"); var d18d = Path.Combine(dir, "d18.dst");
            File.Delete(d18s); File.Delete(d18d);
            CreateDense(d18s, (1 << 20) + 321, 53);
            AddStream(d18s, "meta", 1234, 54);
            AddStream(d18s, "empty", 0, 55);
            string e18;
            bool ok18 = CloneOk(d18s, d18d, out e18);
            if (!ok18) Console.WriteLine("      ERR: " + e18);
            Check("ADS clone exit", ok18);
            if (File.Exists(d18d))
            {
                Check("ADS meta exists", StreamExists(d18d, "meta"));
                Check("ADS meta size", StreamSize(d18d, "meta") == 1234);
                Check("ADS meta content", StreamEqual(d18s, "meta", d18d, "meta"));
                Check("ADS empty exists", StreamExists(d18d, "empty"));
                Check("ADS empty size", StreamSize(d18d, "empty") == 0);
                Check("main content equal (SHA256)", Sha(d18s) == Sha(d18d));
                CheckMetadata(d18s, d18d);
            }
            File.Delete(d18s); File.Delete(d18d);

            Console.WriteLine("\n==== RESULT: PASS=" + _pass + " FAIL=" + _fail + " ====");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex.GetType().FullName + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static void AddStream(string path, string name, int size, int seed)
    {
        var h = CreateFileW(path + ":" + name + ":$DATA", GENERIC_READ | GENERIC_WRITE, 0,
            IntPtr.Zero, 2 /* CREATE_ALWAYS */, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new IOException("create stream failed: " + Marshal.GetLastWin32Error());
        try
        {
            if (size > 0)
            {
                var buf = new byte[size];
                new Random(seed).NextBytes(buf);
                uint w;
                if (!WriteFile(h, buf, (uint)buf.Length, out w, IntPtr.Zero) || w != buf.Length)
                    throw new IOException("write stream failed: " + Marshal.GetLastWin32Error());
            }
        }
        finally { CloseHandle(h); }
    }

    private static IntPtr OpenStream(string path, string name)
    {
        return CreateFileW(path + ":" + name + ":$DATA", GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    }

    private static bool StreamExists(string path, string name)
    {
        var h = OpenStream(path, name);
        if (h == new IntPtr(-1)) return false;
        CloseHandle(h);
        return true;
    }

    private static long StreamSize(string path, string name)
    {
        var h = OpenStream(path, name);
        if (h == new IntPtr(-1)) return -1;
        ulong sz;
        bool ok = GetFileSizeEx(h, out sz);
        CloseHandle(h);
        return ok ? (long)sz : -1;
    }

    private static bool StreamEqual(string a, string nameA, string b, string nameB)
    {
        var ha = OpenStream(a, nameA);
        var hb = OpenStream(b, nameB);
        if (ha == new IntPtr(-1) || hb == new IntPtr(-1)) { if (ha != new IntPtr(-1)) CloseHandle(ha); if (hb != new IntPtr(-1)) CloseHandle(hb); return false; }
        try
        {
            var ba = new byte[1 << 16]; var bb = new byte[1 << 16];
            while (true)
            {
                uint na, nb;
                bool oka = ReadFile(ha, ba, (uint)ba.Length, out na, IntPtr.Zero);
                bool okb = ReadFile(hb, bb, (uint)bb.Length, out nb, IntPtr.Zero);
                if (!oka || !okb || na != nb) return false;
                if (na == 0) return true;
                for (int i = 0; i < (int)na; i++) if (ba[i] != bb[i]) return false;
            }
        }
        finally { CloseHandle(ha); CloseHandle(hb); }
    }

    // Raw FSCTL_DUPLICATE_EXTENTS_TO_FILE with the OLD round-up strategy,
    // bypassing the engine, to record real ReFS behavior on non-aligned sizes.
    private static int RawRoundUpClone(string src, string dst)
    {
        IntPtr hIn = IntPtr.Zero, hOut = IntPtr.Zero, buf = IntPtr.Zero;
        try
        {
            hIn = CreateFileW(src, GENERIC_READ, 1, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hIn == new IntPtr(-1)) return Marshal.GetLastWin32Error();
            ulong size; GetFileSizeEx(hIn, out size);
            hOut = CreateFileW(dst, GENERIC_READ | GENERIC_WRITE | 0x10000, 0, IntPtr.Zero, CREATE_ALWAYS, 0, IntPtr.Zero);
            if (hOut == new IntPtr(-1)) return Marshal.GetLastWin32Error();
            ulong cluster = 4096;
            buf = Marshal.AllocHGlobal(16);
            uint dummy;
            if (DeviceIoControl(hIn, FSCTL_GET_INTEGRITY_INFORMATION, IntPtr.Zero, 0, buf, 16, out dummy, IntPtr.Zero))
                cluster = (uint)Marshal.ReadInt32(buf, 12);
            Marshal.FreeHGlobal(buf);
            ulong aligned = ((size + cluster - 1) / cluster) * cluster;
            // EOF = aligned
            buf = Marshal.AllocHGlobal(8);
            Marshal.WriteInt64(buf, 0, (long)aligned);
            SetFileInformationByHandle(hOut, 6, buf, 8);
            Marshal.FreeHGlobal(buf);
            // dup extents over the whole aligned range
            buf = Marshal.AllocHGlobal(32);
            Marshal.WriteIntPtr(buf, 0, hIn);
            Marshal.WriteInt64(buf, 8, 0);
            Marshal.WriteInt64(buf, 16, 0);
            Marshal.WriteInt64(buf, 24, (long)aligned);
            bool ok = DeviceIoControl(hOut, FSCTL_DUPLICATE_EXTENTS_TO_FILE, buf, 32, IntPtr.Zero, 0, out dummy, IntPtr.Zero);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
            return err;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (hIn != IntPtr.Zero && hIn != new IntPtr(-1)) CloseHandle(hIn);
            if (hOut != IntPtr.Zero && hOut != new IntPtr(-1)) CloseHandle(hOut);
            File.Delete(dst);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(IntPtr h, int cls, IntPtr info, uint sz);

    private static bool SampleEqual(string a, string b)
    {
        long size = new FileInfo(a).Length;
        var rng = new Random(42);
        var offs = new System.Collections.Generic.List<long> { 0, size / 2, size - (1 << 20) };
        for (int i = 0; i < 5; i++) offs.Add((long)(rng.NextDouble() * (size - (1 << 20))));
        var ba = new byte[1 << 20]; var bb = new byte[1 << 20];
        using (var fa = File.OpenRead(a))
        using (var fb = File.OpenRead(b))
        {
            foreach (var off in offs)
            {
                fa.Seek(off, SeekOrigin.Begin); fb.Seek(off, SeekOrigin.Begin);
                int na = fa.Read(ba, 0, ba.Length); int nb = fb.Read(bb, 0, bb.Length);
                if (na != nb) return false;
                for (int i = 0; i < na; i++) if (ba[i] != bb[i]) return false;
            }
        }
        return true;
    }
}
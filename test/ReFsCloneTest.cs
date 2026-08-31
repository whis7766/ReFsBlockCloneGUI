// Console harness that compiles the SAME ReFsBlockCloner.cs used by the GUI and
// verifies it end-to-end on a real ReFS volume.
// Usage: ReFsCloneTest.exe <ReFS-drive-letter e.g. D> [quick]
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    private static void Check(string name, bool cond)
    {
        Console.WriteLine((cond ? "  [PASS] " : "  [FAIL] ") + name);
        if (cond) _pass++; else _fail++;
    }

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
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, CREATE_ALWAYS = 2, FILE_BEGIN = 0;
        const uint FSCTL_SET_SPARSE = 0x000900C4;
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

    private static int Main(string[] args)
    {
        try
        {
            string drive = (args.Length > 0 ? args[0] : "D").TrimEnd('\\', ':') + ":\\";
            string dir = Path.Combine(drive, "reftest");
            bool quick = args.Length > 1 && args[1] == "quick";
            Directory.CreateDirectory(dir);
            Console.WriteLine("== ReFS block-clone engine verification on " + drive + " (quick=" + quick + ") ==");

            // 1) dense non-aligned (100MB + 12345)
            Console.WriteLine("\n[1] dense non-aligned 100MB+12345");
            var d1s = Path.Combine(dir, "d1.src"); var d1d = Path.Combine(dir, "d1.dst");
            File.Delete(d1s); File.Delete(d1d);
            CreateDense(d1s, 100L * 1024 * 1024 + 12345, 7);
            string e1;
            bool ok1 = CloneOk(d1s, d1d, out e1);
            if (!ok1) Console.WriteLine("      ERR: " + e1);
            Check("clone exit", ok1);
            LenOk(d1d, new FileInfo(d1s).Length, "dst");
            if (File.Exists(d1d)) Check("content equal (SHA256)", Sha(d1s) == Sha(d1d));
            File.Delete(d1s); File.Delete(d1d);

            // 2) dense 8.231GB non-aligned (full scale)
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

            // 3) sparse non-aligned, tail partial cluster has data
            Console.WriteLine("\n[3] sparse 100MB+12345, tail partial has data");
            var d3s = Path.Combine(dir, "d3.src"); var d3d = Path.Combine(dir, "d3.dst");
            File.Delete(d3s); File.Delete(d3d);
            long m100 = 100L * 1024 * 1024;
            DoCreateSparse(d3s, m100 + 12345, new long[][] { new long[] { 0, 1 << 20 }, new long[] { m100 - 4096, 16384 } });
            string e3;
            bool ok3 = CloneOk(d3s, d3d, out e3);
            if (!ok3) Console.WriteLine("      ERR: " + e3);
            Check("clone exit", ok3);
            LenOk(d3d, new FileInfo(d3s).Length, "dst");
            if (File.Exists(d3d)) Check("dst still SPARSE", IsSparse(d3d));
            if (File.Exists(d3d)) Check("content equal (SHA256)", Sha(d3s) == Sha(d3d));
            File.Delete(d3s); File.Delete(d3d);

            // 4) zero-byte
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

            // 5) cross-volume must be REJECTED (ReFS -> NTFS)
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

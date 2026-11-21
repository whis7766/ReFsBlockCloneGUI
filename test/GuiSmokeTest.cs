// Headless GUI verification: drives the REAL MainForm through its real clone
// pipeline on a ReFS volume and renders each state to PNG via DrawToBitmap
// (works without an interactive desktop, e.g. CI session 0).
// Usage: GuiSmokeTest.exe <ReFS-drive e.g. R> <png-output-dir> [ntfs-drive e.g. C]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReFsBlockClone;

internal static class GuiSmokeTest
{
    private static int _pass, _fail;
    private static readonly List<string> Dialogs = new List<string>();
    private static MainForm _form;
    private static string _outDir;

    private static void Check(string name, bool cond)
    {
        Console.WriteLine((cond ? "  [PASS] " : "  [FAIL] ") + name);
        if (cond) _pass++; else _fail++;
    }

    private static T Find<T>(string name) where T : Control
    {
        var found = _form.Controls.Find(name, true);
        return found.Length > 0 ? (T)found[0] : null;
    }

    private static void Snap(string name)
    {
        Application.DoEvents();
        // Form.DrawToBitmap prints the WHOLE window (non-client title bar
        // included), so the bitmap must be window-sized, not client-sized,
        // or the bottom (status bar) gets cropped out.
        using (var bmp = new Bitmap(_form.Width, _form.Height))
        {
            _form.DrawToBitmap(bmp, new Rectangle(0, 0, _form.Width, _form.Height));
            bmp.Save(Path.Combine(_outDir, name + ".png"), ImageFormat.Png);
        }
        Console.WriteLine("  [SNAP] " + name + ".png");
    }

    private static void PumpUntil(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static string Sha(string p)
    {
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(p))
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
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

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string drive = (args.Length > 0 ? args[0] : "R").TrimEnd('\\', ':') + ":\\";
            _outDir = args.Length > 1 ? args[1] : "gui-shots";
            string ntfs = (args.Length > 2 ? args[2] : "C").TrimEnd('\\', ':');
            Directory.CreateDirectory(_outDir);
            string dir = Path.Combine(drive, "guismoke");
            Directory.CreateDirectory(dir);

            UiHooks.ShowMessage = (o, t, c, b, i) =>
            {
                Dialogs.Add(c + "|" + t);
                return b == MessageBoxButtons.YesNo ? DialogResult.Yes : DialogResult.OK;
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _form = new MainForm();
            _form.Show();
            Application.DoEvents();

            Console.WriteLine("== GUI smoke test on " + drive + " ==");
            Snap("01-initial");
            Check("initial: clone button disabled", !Find<Button>("btnClone").Enabled);
            Check("initial: cancel button disabled", !Find<Button>("btnCancel").Enabled);

            // ---- filled state + volume probe + auto suggestion ----
            string smallSrc = Path.Combine(dir, "small.src");
            string smallDst = Path.Combine(dir, "small.dst");
            File.Delete(smallSrc); File.Delete(smallDst);
            CreateDense(smallSrc, 64L * 1024 * 1024 + 321, 1);
            Find<TextBox>("txtSource").Text = smallSrc;
            var lblVolSrc = Find<Label>("lblVolSrc");
            var lblVolDst = Find<Label>("lblVolDst");
            PumpUntil(() => lblVolSrc.Text.Contains("ReFS"), 5000);
            Check("volume probe shows ReFS", lblVolSrc.Text.Contains("ReFS"));
            Check("same-volume indicator green", lblVolDst.Text.Contains("同卷"));
            Check("dest auto-suggested", Find<TextBox>("txtDest").Text.Contains(".clone"));
            Check("clone button enabled", Find<Button>("btnClone").Enabled);
            Snap("02-filled");

            // ---- real success run ----
            Find<TextBox>("txtDest").Text = smallDst;
            Dialogs.Clear();
            var t1 = _form.DoCloneAsync();
            PumpUntil(() => t1.IsCompleted, 30000);
            Check("success task completed", t1.IsCompleted && !t1.IsFaulted);
            Check("dst exists", File.Exists(smallDst));
            Check("dst size equal", File.Exists(smallDst) &&
                new FileInfo(smallDst).Length == new FileInfo(smallSrc).Length);
            Check("dst SHA256 equal", File.Exists(smallDst) && Sha(smallSrc) == Sha(smallDst));
            Check("no modal dialog on success", Dialogs.Count == 0);
            Check("status shows completion", _form.StatusText.Contains("完成"));
            Check("log shows completion", _form.LogText.Contains("克隆完成"));
            Snap("03-success");

            // ---- mid-clone progress, then cancel ----
            string bigSrc = Path.Combine(dir, "big.src");
            string bigDst = Path.Combine(dir, "big.dst");
            File.Delete(bigSrc); File.Delete(bigDst);
            CreateDense(bigSrc, 1024L * 1024 * 1024 + 77, 2);
            var gateStarted = new ManualResetEventSlim(false);
            var gateRelease = new ManualResetEventSlim(false);
            // Runs on the WORKER thread: update UI asynchronously, then pause the
            // clone loop so the progress state can be captured deterministically.
            MainForm.ProgressOverride = p =>
            {
                _form.ApplyProgress(p);
                if (!gateStarted.IsSet)
                {
                    gateStarted.Set();
                    gateRelease.Wait(20000);
                }
            };
            Find<TextBox>("txtSource").Text = bigSrc;
            Find<TextBox>("txtDest").Text = bigDst;
            Dialogs.Clear();
            var t2 = _form.DoCloneAsync();
            PumpUntil(() => gateStarted.IsSet, 30000);
            Check("progress gate reached", gateStarted.IsSet);
            PumpUntil(() => Find<ProgressBar>("progressBar").Value > 0, 5000);
            Check("progress bar advanced", Find<ProgressBar>("progressBar").Value > 0);
            Check("cancel button enabled while busy", Find<Button>("btnCancel").Enabled);
            Snap("04-progress");
            Find<Button>("btnCancel").PerformClick();
            gateRelease.Set();
            PumpUntil(() => t2.IsCompleted, 30000);
            MainForm.ProgressOverride = null;
            Check("cancel task completed", t2.IsCompleted && !t2.IsFaulted);
            Check("no orphan after cancel", !File.Exists(bigDst));
            Check("status shows cancelled", _form.StatusText.Contains("取消"));
            Snap("05-cancelled");

            // ---- cross-volume error path ----
            string ntfsDst = ntfs + ":\\guismoke_cross_" + Guid.NewGuid().ToString("N") + ".dst";
            Find<TextBox>("txtSource").Text = smallSrc;
            Find<TextBox>("txtDest").Text = ntfsDst;
            Dialogs.Clear();
            var t3 = _form.DoCloneAsync();
            PumpUntil(() => t3.IsCompleted, 30000);
            Check("cross-volume dst absent", !File.Exists(ntfsDst));
            Check("error dialog recorded", Dialogs.Exists(d => d.StartsWith("错误")));
            Check("log shows failure", _form.LogText.Contains("失败"));
            // let the debounced volume probe refresh so the screenshot shows
            // the red cross-volume indicator, not the stale same-volume one
            PumpUntil(() => Find<Label>("lblVolDst").Text.Contains("不同卷"), 2000);
            Snap("06-error");
            File.Delete(ntfsDst);

            // ---- destination-exists validation (use a file that is NOT the source) ----
            Find<TextBox>("txtDest").Text = bigSrc;
            Dialogs.Clear();
            var t4 = _form.DoCloneAsync();
            PumpUntil(() => t4.IsCompleted, 10000);
            Check("exists dialog recorded", Dialogs.Exists(d => d.Contains("已存在")));
            Snap("07-exists");

            // ---- physical mode: cross-volume copy must succeed without warning ----
            Find<CheckBox>("chkPhysical").Checked = true;
            Find<TextBox>("txtSource").Text = smallSrc;
            string physDst = ntfs + ":\\guismoke_phys_" + Guid.NewGuid().ToString("N") + ".dst";
            Find<TextBox>("txtDest").Text = physDst;
            Dialogs.Clear();
            var t5 = _form.DoCloneAsync();
            PumpUntil(() => t5.IsCompleted, 60000);
            Check("physical cross-volume success", File.Exists(physDst));
            Check("physical SHA256 equal", File.Exists(physDst) && Sha(smallSrc) == Sha(physDst));
            Check("no cross-volume warning in physical mode", Dialogs.Count == 0);
            Check("log shows physical mode", _form.LogText.Contains("物理复制"));
            Snap("09-physical");
            File.Delete(physDst);
            Find<CheckBox>("chkPhysical").Checked = false;

            // ---- minimum-size layout ----
            _form.Size = _form.MinimumSize;
            Application.DoEvents();
            Snap("08-minsize");

            File.Delete(smallSrc); File.Delete(smallDst);
            File.Delete(bigSrc); File.Delete(bigDst);

            Console.WriteLine("\n==== GUI RESULT: PASS=" + _pass + " FAIL=" + _fail + " ====");
            _form.Close();
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex.GetType().FullName + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 2;
        }
    }
}
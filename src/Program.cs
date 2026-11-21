using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ReFsBlockClone
{
    internal static class Program
    {
        private const string MutexName = @"Local\ReFsBlockCloneGUI_SingleInstance";

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [STAThread]
        private static int Main(string[] args)
        {
            // Headless mode: ReFsBlockCloneGUI.exe <src> <dst> [--physical]
            if (args != null && args.Length >= 2)
                return RunHeadless(args);

            // Single-instance guard: a second launch activates the existing window.
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                    {
                        BringExistingToFront();
                        return 0;
                    }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            return 0;
        }

        private static void BringExistingToFront()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id == current.Id) continue;
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        SetForegroundWindow(p.MainWindowHandle);
                        break;
                    }
                }
            }
            catch { }
        }

        private static int RunHeadless(string[] args)
        {
            bool physical = false;
            var paths = new System.Collections.Generic.List<string>();
            foreach (var a in args)
            {
                if (string.Equals(a, "--physical", StringComparison.OrdinalIgnoreCase)) physical = true;
                else paths.Add(a.Trim('"'));
            }
            if (paths.Count < 2) return 1;
            string src = paths[0], dst = paths[1];

            var logLines = new System.Collections.Generic.List<string>();
            logLines.Add(string.Format("[{0}] 无头克隆（{1}）：{2} -> {3}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                physical ? "物理复制" : "块克隆", src, dst));

            int exit = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                var cloner = new RefsBlockCloner(s => logLines.Add("  " + s));
                if (physical) cloner.ClonePhysical(src, dst);
                else cloner.Clone(src, dst);
                sw.Stop();
                logLines.Add(string.Format("成功，用时 {0:0.000} 秒。", sw.Elapsed.TotalSeconds));
            }
            catch (Exception ex)
            {
                sw.Stop();
                logLines.Add("失败：" + ex.Message);
                exit = 1;
            }

            try
            {
                var enc = new UTF8Encoding(true); // BOM so Notepad renders Chinese correctly
                if (File.Exists("refsclone_headless.log"))
                    File.AppendAllLines("refsclone_headless.log", logLines, enc);
                else
                    File.WriteAllLines("refsclone_headless.log", logLines, enc);
            }
            catch { }
            return exit;
        }
    }
}

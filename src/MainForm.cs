using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReFsBlockClone
{
    /// <summary>
    /// Dialog indirection so the headless GUI test harness can record and
    /// answer message boxes instead of blocking on a real desktop.
    /// </summary>
    internal static class UiHooks
    {
        public static Func<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon, DialogResult> ShowMessage =
            (owner, text, caption, buttons, icon) => MessageBox.Show(owner, text, caption, buttons, icon);
    }

    internal sealed class InlineProgress : IProgress<CloneProgress>
    {
        private readonly Action<CloneProgress> _a;
        public InlineProgress(Action<CloneProgress> a) { _a = a; }
        public void Report(CloneProgress v) { _a(v); }
    }

    public sealed class MainForm : Form
    {
        private readonly TextBox _txtSource = new TextBox { Name = "txtSource" };
        private readonly TextBox _txtDest = new TextBox { Name = "txtDest" };
        private readonly Button _btnClone = new Button { Name = "btnClone" };
        private readonly Button _btnCancel = new Button { Name = "btnCancel" };
        private readonly ProgressBar _progress = new ProgressBar { Name = "progressBar" };
        private readonly Label _lblPercent = new Label { Name = "lblPercent" };
        private readonly Label _lblVolSrc = new Label { Name = "lblVolSrc" };
        private readonly Label _lblVolDst = new Label { Name = "lblVolDst" };
        private readonly TextBox _txtLog = new TextBox { Name = "txtLog" };
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel { Name = "statusLabel" };

        private readonly System.Windows.Forms.Timer _probeTimer = new System.Windows.Forms.Timer { Interval = 300 };
        private int _probeSeq;
        private VolumeInfo _srcInfo, _dstInfo;
        private bool _busy;
        private bool _destAuto;
        private bool _settingDest;
        private CancellationTokenSource _cts;

        /// <summary>Test hook: replaces the default progress UI update.</summary>
        internal static Action<CloneProgress> ProgressOverride;

        internal string LogText { get { return _txtLog.Text; } }
        internal string StatusText { get { return _status.Text; } }

        public MainForm()
        {
            BuildUi();
            _probeTimer.Tick += async (s, e) => { _probeTimer.Stop(); await ProbeVolumesAsync(); };
        }

        private void BuildUi()
        {
            Text = "ReFS 块克隆";
            ClientSize = new Size(800, 620);
            MinimumSize = new Size(680, 540);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            KeyPreview = true;
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && _busy) CancelClone(); };

            // ---- source / destination groups ----
            var grpSource = new GroupBox { Text = "源文件（ReFS 卷上的原文件）", Height = 66, Dock = DockStyle.Fill };
            var tblSrc = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8, 4, 8, 6) };
            tblSrc.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tblSrc.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            _txtSource.Dock = DockStyle.Fill;
            _txtSource.AllowDrop = true;
            _txtSource.DragEnter += OnDragEnter;
            _txtSource.DragDrop += OnDragDrop;
            var btnBrowseSource = new Button { Text = "浏览...", Dock = DockStyle.Fill, Name = "btnBrowseSource" };
            btnBrowseSource.Click += (s, e) => BrowseSource();
            tblSrc.Controls.Add(_txtSource, 0, 0);
            tblSrc.Controls.Add(btnBrowseSource, 1, 0);
            grpSource.Controls.Add(tblSrc);

            var grpDest = new GroupBox { Text = "目标文件（同一 ReFS 卷上的新路径，不覆盖已有文件）", Height = 66, Dock = DockStyle.Fill };
            var tblDst = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8, 4, 8, 6) };
            tblDst.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tblDst.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            _txtDest.Dock = DockStyle.Fill;
            var btnBrowseDest = new Button { Text = "另存为...", Dock = DockStyle.Fill, Name = "btnBrowseDest" };
            btnBrowseDest.Click += (s, e) =>
            {
                using (var dlg = new SaveFileDialog
                {
                    Title = "选择目标路径",
                    OverwritePrompt = false,
                    CheckPathExists = true,
                    Filter = "所有文件 (*.*)|*.*"
                })
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _destAuto = false;
                        _txtDest.Text = dlg.FileName;
                    }
            };
            tblDst.Controls.Add(_txtDest, 0, 0);
            tblDst.Controls.Add(btnBrowseDest, 1, 0);
            grpDest.Controls.Add(tblDst);

            // ---- volume info ----
            var grpVol = new GroupBox { Text = "卷信息", Height = 78, Dock = DockStyle.Fill };
            var tblVol = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 2, 8, 4) };
            tblVol.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            tblVol.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            _lblVolSrc.Dock = DockStyle.Fill;
            _lblVolSrc.TextAlign = ContentAlignment.MiddleLeft;
            _lblVolSrc.Text = "源卷：待探测";
            _lblVolDst.Dock = DockStyle.Fill;
            _lblVolDst.TextAlign = ContentAlignment.MiddleLeft;
            _lblVolDst.Text = "目标卷：待探测";
            tblVol.Controls.Add(_lblVolSrc, 0, 0);
            tblVol.Controls.Add(_lblVolDst, 0, 1);
            grpVol.Controls.Add(tblVol);

            // ---- actions row ----
            var pnlActions = new TableLayoutPanel { Height = 44, Dock = DockStyle.Fill, ColumnCount = 4 };
            pnlActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
            pnlActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
            pnlActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pnlActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64F));
            _btnClone.Text = "开始克隆";
            _btnClone.Dock = DockStyle.Fill;
            _btnClone.FlatStyle = FlatStyle.Flat;
            _btnClone.BackColor = Color.FromArgb(0, 120, 212);
            _btnClone.ForeColor = Color.White;
            _btnClone.FlatAppearance.BorderSize = 0;
            _btnClone.Enabled = false;
            _btnClone.Click += async (s, e) => await DoCloneAsync();
            _btnCancel.Text = "取消";
            _btnCancel.Dock = DockStyle.Fill;
            _btnCancel.Enabled = false;
            _btnCancel.Click += (s, e) => CancelClone();
            _progress.Dock = DockStyle.Fill;
            _progress.Minimum = 0;
            _progress.Maximum = 1000;
            _lblPercent.Dock = DockStyle.Fill;
            _lblPercent.TextAlign = ContentAlignment.MiddleRight;
            _lblPercent.Text = "0%";
            pnlActions.Controls.Add(_btnClone, 0, 0);
            pnlActions.Controls.Add(_btnCancel, 1, 0);
            pnlActions.Controls.Add(_progress, 2, 0);
            pnlActions.Controls.Add(_lblPercent, 3, 0);

            // ---- log ----
            var grpLog = new GroupBox { Text = "日志", Dock = DockStyle.Fill };
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Font = new Font("Consolas", 9F);
            _txtLog.BackColor = Color.White;
            grpLog.Controls.Add(_txtLog);

            // ---- root layout ----
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(grpSource, 0, 0);
            root.Controls.Add(grpDest, 0, 1);
            root.Controls.Add(grpVol, 0, 2);
            root.Controls.Add(pnlActions, 0, 3);
            root.Controls.Add(grpLog, 0, 4);

            var strip = new StatusStrip { Name = "statusStrip" };
            _status.Text = "就绪。选择源文件与目标路径后开始克隆。";
            _status.Spring = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            strip.Items.Add(_status);

            Controls.Add(root);
            Controls.Add(strip);

            _txtSource.TextChanged += (s, e) => { SuggestDest(); KickProbe(); UpdateCloneEnabled(); };
            _txtDest.TextChanged += (s, e) => { if (!_settingDest) _destAuto = false; KickProbe(); UpdateCloneEnabled(); };
            AcceptButton = _btnClone;
        }

        private void BrowseSource()
        {
            using (var dlg = new OpenFileDialog { Title = "选择源文件", CheckFileExists = true, Filter = "所有文件 (*.*)|*.*" })
                if (dlg.ShowDialog(this) == DialogResult.OK) _txtSource.Text = dlg.FileName;
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) _txtSource.Text = files[0];
        }

        private void SuggestDest()
        {
            if (!_destAuto && _txtDest.Text.Trim().Length > 0) return;
            try
            {
                string src = _txtSource.Text.Trim().Trim('"');
                if (src.Length == 0) return;
                string dir = Path.GetDirectoryName(src);
                string name = Path.GetFileNameWithoutExtension(src) + ".clone" + Path.GetExtension(src);
                if (string.IsNullOrEmpty(dir)) return;
                _settingDest = true;
                _destAuto = true;
                _txtDest.Text = Path.Combine(dir, name);
                _settingDest = false;
            }
            catch { _settingDest = false; }
        }

        private void KickProbe()
        {
            _probeTimer.Stop();
            _probeTimer.Start();
        }

        private async Task ProbeVolumesAsync()
        {
            string src = _txtSource.Text.Trim().Trim('"');
            string dst = _txtDest.Text.Trim().Trim('"');
            int seq = ++_probeSeq;
            var result = await Task.Run(() =>
            {
                VolumeInfo si = null, di = null;
                if (src.Length > 0) RefsBlockCloner.TryProbe(src, out si);
                if (dst.Length > 0) RefsBlockCloner.TryProbe(dst, out di);
                return new[] { si, di };
            });
            if (seq != _probeSeq || IsDisposed) return;
            _srcInfo = result[0];
            _dstInfo = result[1];

            if (_srcInfo == null)
            {
                _lblVolSrc.Text = "源卷：无法探测（路径不存在或不可访问）";
                _lblVolSrc.ForeColor = Color.FromArgb(160, 60, 60);
            }
            else
            {
                _lblVolSrc.Text = string.Format("源卷：{0} · 簇 {1} B · 完整性 streams：{2} · 稀疏：{3}",
                    _srcInfo.FileSystem, _srcInfo.ClusterSize,
                    _srcInfo.IntegrityEnabled ? "开" : "关", _srcInfo.IsSparse ? "是" : "否");
                _lblVolSrc.ForeColor = _srcInfo.SupportsBlockClone ? Color.FromArgb(30, 110, 50) : Color.FromArgb(160, 60, 60);
            }

            if (_dstInfo == null)
            {
                _lblVolDst.Text = "目标卷：待探测";
                _lblVolDst.ForeColor = SystemColors.ControlText;
            }
            else if (_srcInfo != null && _dstInfo.Serial == _srcInfo.Serial && _dstInfo.SupportsBlockClone)
            {
                _lblVolDst.Text = string.Format("目标卷：与源同卷 ✓（{0}，可块克隆）", _dstInfo.FileSystem);
                _lblVolDst.ForeColor = Color.FromArgb(30, 110, 50);
            }
            else
            {
                _lblVolDst.Text = string.Format("目标卷：{0} —— 与源不同卷或不支持块克隆，克隆将被拒绝",
                    _dstInfo.FileSystem);
                _lblVolDst.ForeColor = Color.FromArgb(160, 60, 60);
            }
        }

        private void UpdateCloneEnabled()
        {
            _btnClone.Enabled = !_busy && _txtSource.Text.Trim().Length > 0 && _txtDest.Text.Trim().Length > 0;
        }

        private void CancelClone()
        {
            if (_cts != null) _cts.Cancel();
            _status.Text = "正在取消...";
        }

        private void AppendLog(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)(() => AppendLog(line))); } catch { }
                return;
            }
            _txtLog.AppendText(line + Environment.NewLine);
        }

        internal void ApplyProgress(CloneProgress p)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)(() => ApplyProgress(p))); } catch { }
                return;
            }
            int pct = p.TotalBytes > 0 ? (int)(p.BytesDone * 1000 / p.TotalBytes) : 1000;
            _progress.Value = Math.Max(0, Math.Min(1000, pct));
            _lblPercent.Text = (pct / 10) + "%";
            _status.Text = string.Format("正在克隆：{0:0.0}/{1:0.0} MB",
                p.BytesDone / 1048576.0, p.TotalBytes / 1048576.0);
        }

        internal async Task DoCloneAsync()
        {
            if (_busy) return;

            string src = _txtSource.Text.Trim().Trim('"');
            string dst = _txtDest.Text.Trim().Trim('"');

            if (string.IsNullOrEmpty(src) || !File.Exists(src))
            {
                UiHooks.ShowMessage(this, "请选择或输入已存在的源文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(dst))
            {
                UiHooks.ShowMessage(this, "请选择或输入目标文件路径。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string fullSrc = RefsBlockCloner.NormalizePath(src);
            string fullDst = RefsBlockCloner.NormalizePath(dst);
            if (string.Equals(fullSrc, fullDst, StringComparison.OrdinalIgnoreCase))
            {
                UiHooks.ShowMessage(this, "源文件与目标文件不能相同。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (File.Exists(fullDst))
            {
                UiHooks.ShowMessage(this, "目标文件已存在，块克隆不会覆盖已有文件，请更换目标文件名。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Same-volume check: prefer probed serials; fall back to drive roots.
            bool crossVolume;
            if (_srcInfo != null && _dstInfo != null)
                crossVolume = _srcInfo.Serial != _dstInfo.Serial;
            else
            {
                string sr = Path.GetPathRoot(src), dr = Path.GetPathRoot(dst);
                crossVolume = !string.IsNullOrEmpty(sr) && !string.IsNullOrEmpty(dr) &&
                    !string.Equals(sr, dr, StringComparison.OrdinalIgnoreCase);
            }
            if (crossVolume)
            {
                if (UiHooks.ShowMessage(this,
                    "源文件与目标文件不在同一卷。\n块克隆要求源与目标位于同一 ReFS 卷，继续将被拒绝。仍要继续吗？",
                    "跨卷提示", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            _busy = true;
            UpdateCloneEnabled();
            _btnClone.Text = "正在克隆...";
            _btnCancel.Enabled = true;
            _progress.Value = 0;
            _lblPercent.Text = "0%";
            _txtLog.Clear();
            AppendLog("源文件：   " + src);
            AppendLog("目标文件： " + dst);
            AppendLog("----------------------------------------");

            _cts = new CancellationTokenSource();
            // Synchronous reporter: the override hook (test harness) runs on the
            // worker thread and may pause the clone loop deterministically.
            var progress = new InlineProgress(p =>
            {
                var ov = ProgressOverride;
                if (ov != null) ov(p);
                else ApplyProgress(p);
            });
            var sw = Stopwatch.StartNew();
            try
            {
                await Task.Run(() =>
                {
                    var cloner = new RefsBlockCloner(AppendLog);
                    cloner.Clone(src, dst, progress, _cts.Token);
                });
                sw.Stop();
                AppendLog("----------------------------------------");
                AppendLog(string.Format("克隆完成，用时 {0:0.000} 秒。", sw.Elapsed.TotalSeconds));
                _progress.Value = 1000;
                _lblPercent.Text = "100%";
                _status.Text = string.Format("克隆完成，用时 {0:0.000} 秒。", sw.Elapsed.TotalSeconds);
                _status.ForeColor = Color.FromArgb(30, 110, 50);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                AppendLog("----------------------------------------");
                AppendLog("已取消，半成品目标已自动清理。");
                _status.Text = "已取消，半成品目标已自动清理。";
                _status.ForeColor = SystemColors.ControlText;
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppendLog("----------------------------------------");
                AppendLog("克隆失败：" + ex.Message);
                _status.Text = "克隆失败。";
                _status.ForeColor = Color.FromArgb(160, 60, 60);
                if (!IsDisposed)
                    UiHooks.ShowMessage(this, "克隆失败：\n" + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                _btnClone.Text = "开始克隆";
                _btnCancel.Enabled = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                UpdateCloneEnabled();
            }
        }
    }
}
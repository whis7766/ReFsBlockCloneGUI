using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReFsBlockClone
{
    internal static class UiHooks
    {
#if DEBUG
        public static Func<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon, DialogResult> ShowMessage =
            (owner, text, caption, buttons, icon) => MessageBox.Show(owner, text, caption, buttons, icon);

        private static Func<IWin32Window, string, string, string> _pickOpen = DefaultOpen;
        private static Func<IWin32Window, string, string, string> _pickSave = DefaultSave;

        public static Func<IWin32Window, string, string, string> PickOpenFile
        {
            get { return _pickOpen; }
            set { _pickOpen = value ?? DefaultOpen; }
        }

        public static Func<IWin32Window, string, string, string> PickSaveFile
        {
            get { return _pickSave; }
            set { _pickSave = value ?? DefaultSave; }
        }
#else
        public static DialogResult ShowMessage(IWin32Window owner, string text, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(owner, text, caption, buttons, icon);
        }

        public static string PickOpenFile(IWin32Window owner, string title, string initial)
        {
            return DefaultOpen(owner, title, initial);
        }

        public static string PickSaveFile(IWin32Window owner, string title, string initial)
        {
            return DefaultSave(owner, title, initial);
        }
#endif

        private static string DefaultOpen(IWin32Window owner, string title, string initial)
        {
            using (var d = new OpenFileDialog { Title = title, CheckFileExists = true, Filter = "所有文件 (*.*)|*.*" })
            {
                string dir = SafeDir(initial);
                if (dir != null) d.InitialDirectory = dir;
                if (!string.IsNullOrEmpty(initial) && File.Exists(initial)) d.FileName = initial;
                return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
            }
        }

        private static string DefaultSave(IWin32Window owner, string title, string initial)
        {
            using (var d = new SaveFileDialog
            {
                Title = title,
                OverwritePrompt = false,
                CheckPathExists = true,
                AddExtension = false,
                Filter = "所有文件 (*.*)|*.*"
            })
            {
                string dir = SafeDir(initial);
                if (dir != null) d.InitialDirectory = dir;
                if (!string.IsNullOrEmpty(initial)) d.FileName = Path.GetFileName(initial);
                return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
            }
        }

        private static string SafeDir(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                string dir = Path.GetDirectoryName(path);
                return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
            }
            catch { return null; }
        }
    }

    internal sealed class InlineProgress : IProgress<CloneProgress>
    {
        private readonly Action<CloneProgress> _a;
        public InlineProgress(Action<CloneProgress> a) { _a = a; }
        public void Report(CloneProgress v) { _a(v); }
    }

    internal static class Theme
    {
        public static readonly Color Bg = SystemColors.Control;
        public static readonly Color Card = SystemColors.Window;
        public static readonly Color Border = SystemColors.ControlDark;
        public static readonly Color Text = SystemColors.ControlText;
        public static readonly Color Muted = SystemColors.GrayText;
        public static readonly Color Accent = SystemColors.HotTrack;
        public static readonly Color Good = Color.FromArgb(0, 128, 0);
        public static readonly Color Bad = Color.FromArgb(180, 30, 30);
    }

    public sealed class MainForm : Form
    {
        private static readonly Icon AppIcon = LoadAppIcon();

        private TextBox _txtSource = new TextBox { Name = "txtSource" };
        private TextBox _txtDest = new TextBox { Name = "txtDest" };
        private Button _btnClone = new Button { Name = "btnClone" };
        private Button _btnCancel = new Button { Name = "btnCancel" };
        private Label _lblPercent = new Label { Name = "lblPercent" };
        private Label _lblVolSrc = new Label { Name = "lblVolSrc" };
        private Label _lblVolDst = new Label { Name = "lblVolDst" };
        private TextBox _txtLog = new TextBox { Name = "txtLog" };
        private StatusStrip _statusBar = new StatusStrip { Name = "statusStrip", SizingGrip = false };
        private ToolStripStatusLabel _status = new ToolStripStatusLabel { Name = "statusLabel" };

        private readonly System.Windows.Forms.Timer _probeTimer = new System.Windows.Forms.Timer { Interval = 300 };
        private int _probeSeq;
        private VolumeInfo _srcInfo, _dstInfo;
        private bool _busy;
        private bool _destAuto;
        private bool _settingDest;
        private CancellationTokenSource _cts;

#if DEBUG
        internal static Action<CloneProgress> ProgressOverride;

        internal string LogText { get { return _txtLog.Text; } }
        internal string StatusText { get { return _status.Text; } }
#endif

        public MainForm()
        {
            Icon = AppIcon;
            BuildUi();
            _probeTimer.Tick += async (s, e) => { _probeTimer.Stop(); await ProbeVolumesAsync(); };
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("ReFsBlockClone.app.ico"))
                    if (s != null) return new Icon(s);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
        }

        private void StyleField(TextBox t)
        {
            t.Margin = new Padding(0);
            t.Font = new Font("Microsoft YaHei UI", 10F);
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = SystemColors.Window;
            t.ForeColor = SystemColors.WindowText;
        }

        private Button BrowseButton(string name, string text)
        {
            var b = new Button
            {
                Name = name,
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(78, 0),
                Padding = new Padding(8, 2, 8, 2),
                Margin = new Padding(8, 1, 0, 1),
                UseVisualStyleBackColor = true
            };
            return b;
        }

        private Button ActionButton(string name, string text, int minWidth)
        {
            var b = new Button
            {
                Name = name,
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(minWidth, 0),
                Padding = new Padding(12, 4, 12, 4),
                Margin = new Padding(0, 0, 8, 0),
                UseVisualStyleBackColor = true,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            return b;
        }

        private GroupBox FileGroup(string caption, TextBox field, Button browse)
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            field.Dock = DockStyle.Fill;
            grid.Controls.Add(field, 0, 0);
            grid.Controls.Add(browse, 1, 0);

            var box = new GroupBox
            {
                Text = caption,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 10)
            };
            box.Controls.Add(grid);
            return box;
        }

        private GroupBox LogGroup(TextBox log)
        {
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Dock = DockStyle.Fill;
            log.BorderStyle = BorderStyle.None;
            log.Font = new Font("Consolas", 9F);
            log.BackColor = SystemColors.Window;
            log.ForeColor = SystemColors.WindowText;
            log.Margin = new Padding(0);

            var box = new GroupBox
            {
                Text = "日志",
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 6, 10, 10)
            };
            box.Controls.Add(log);
            return box;
        }

        private void BuildUi()
        {
            Text = "";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(820, 560);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SystemColors.Control;
            Font = new Font("Microsoft YaHei UI", 9F);
            KeyPreview = true;
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && _busy) CancelClone(); };

            StyleField(_txtSource);
            StyleField(_txtDest);
            _txtSource.AllowDrop = true;
            _txtSource.DragEnter += OnDragEnter;
            _txtSource.DragDrop += OnDragDrop;

            var btnBrowseSource = BrowseButton("btnBrowseSource", "浏览");
            btnBrowseSource.Click += (s, e) =>
            {
                string p = UiHooks.PickOpenFile(this, "选择源文件", _txtSource.Text.Trim());
                if (!string.IsNullOrEmpty(p)) _txtSource.Text = p;
            };

            var btnBrowseDest = BrowseButton("btnBrowseDest", "另存为");
            btnBrowseDest.Click += (s, e) =>
            {
                string p = UiHooks.PickSaveFile(this, "选择目标路径", _txtDest.Text.Trim());
                if (string.IsNullOrEmpty(p)) return;
                if (File.Exists(RefsBlockCloner.NormalizePath(p)))
                {
                    UiHooks.ShowMessage(this, "目标文件已存在，请更换目标文件名。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _status.Text = "目标文件已存在。";
                    _status.ForeColor = Theme.Bad;
                    _lblPercent.Text = "";
                    return;
                }
                _destAuto = false;
                _txtDest.Text = p;
            };

            var srcGroup = FileGroup("源文件", _txtSource, btnBrowseSource);
            var dstGroup = FileGroup("目标文件", _txtDest, btnBrowseDest);

            _lblVolSrc.AutoSize = false;
            _lblVolSrc.Dock = DockStyle.Fill;
            _lblVolSrc.TextAlign = ContentAlignment.MiddleLeft;
            _lblVolSrc.Font = new Font("Microsoft YaHei UI", 8.5F);
            _lblVolSrc.ForeColor = Theme.Muted;
            _lblVolSrc.Text = "源文件：待选择";
            _lblVolSrc.Margin = new Padding(2, 4, 2, 0);

            _lblVolDst.AutoSize = false;
            _lblVolDst.Dock = DockStyle.Fill;
            _lblVolDst.TextAlign = ContentAlignment.MiddleLeft;
            _lblVolDst.Font = new Font("Microsoft YaHei UI", 8.5F);
            _lblVolDst.ForeColor = Theme.Muted;
            _lblVolDst.Text = "目标：待选择";
            _lblVolDst.Margin = new Padding(2, 0, 2, 2);

            var meta = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0)
            };
            meta.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            meta.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            meta.Controls.Add(_lblVolSrc, 0, 0);
            meta.Controls.Add(_lblVolDst, 0, 1);

            _btnClone = ActionButton("btnClone", "开始克隆", 124);
            _btnClone.Enabled = false;
            _btnClone.Click += async (s, e) => await DoCloneAsync();

            _btnCancel = ActionButton("btnCancel", "取消", 78);
            _btnCancel.Enabled = false;
            _btnCancel.Margin = new Padding(0, 0, 0, 0);
            _btnCancel.Click += (s, e) => CancelClone();

            _lblPercent.AutoSize = true;
            _lblPercent.Anchor = AnchorStyles.Right;
            _lblPercent.TextAlign = ContentAlignment.MiddleRight;
            _lblPercent.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            _lblPercent.ForeColor = Theme.Muted;
            _lblPercent.Text = "";
            _lblPercent.Margin = new Padding(8, 0, 2, 0);

            var actions = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0, 6, 0, 6)
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.Controls.Add(_btnClone, 0, 0);
            actions.Controls.Add(_btnCancel, 1, 0);
            actions.Controls.Add(_lblPercent, 3, 0);

            var logGroup = LogGroup(_txtLog);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(12, 10, 12, 8),
                BackColor = SystemColors.Control
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(srcGroup, 0, 0);
            root.Controls.Add(dstGroup, 0, 1);
            root.Controls.Add(meta, 0, 2);
            root.Controls.Add(actions, 0, 3);
            root.Controls.Add(logGroup, 0, 4);

            _status.Font = new Font("Microsoft YaHei UI", 8.5F);
            _status.ForeColor = Theme.Muted;
            _status.Text = "就绪";
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Spring = true;
            _status.Margin = new Padding(0);
            _statusBar.Items.Add(_status);
            _statusBar.BackColor = SystemColors.Control;
            _statusBar.Dock = DockStyle.Fill;
            _statusBar.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;

            var shell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = SystemColors.Control
            };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.Controls.Add(root, 0, 0);
            shell.Controls.Add(_statusBar, 0, 1);

            Controls.Add(shell);
            srcGroup.Margin = new Padding(0, 0, 0, 6);
            dstGroup.Margin = new Padding(0, 0, 0, 4);

            _txtSource.TextChanged += (s, e) => { SuggestDest(); KickProbe(); UpdateCloneEnabled(); };
            _txtDest.TextChanged += (s, e) => { if (!_settingDest) _destAuto = false; KickProbe(); UpdateCloneEnabled(); };
            AcceptButton = _btnClone;
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
                if (src.Length == 0)
                {
                    if (_destAuto)
                    {
                        _settingDest = true;
                        _destAuto = false;
                        _txtDest.Text = "";
                        _settingDest = false;
                    }
                    return;
                }
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

            if (src.Length == 0)
            {
                _lblVolSrc.Text = "源文件：待选择";
                _lblVolSrc.ForeColor = Theme.Muted;
            }
            else if (_srcInfo == null)
            {
                _lblVolSrc.Text = "源文件：无法探测";
                _lblVolSrc.ForeColor = Theme.Bad;
            }
            else if (!_srcInfo.PathExists)
            {
                _lblVolSrc.Text = "源文件：不存在";
                _lblVolSrc.ForeColor = Theme.Bad;
            }
            else if (_srcInfo.IsDirectory)
            {
                _lblVolSrc.Text = "源文件：路径是目录";
                _lblVolSrc.ForeColor = Theme.Bad;
            }
            else
            {
                _lblVolSrc.Text = string.Format("源文件：{0} · 簇 {1} B · 完整性 {2} · 稀疏 {3}",
                    _srcInfo.FileSystem, _srcInfo.ClusterSize,
                    _srcInfo.IntegrityEnabled ? "开" : "关", _srcInfo.IsSparse ? "是" : "否");
                _lblVolSrc.ForeColor = _srcInfo.SupportsBlockClone ? Theme.Muted : Theme.Bad;
            }

            if (dst.Length == 0)
            {
                _lblVolDst.Text = "目标：待选择";
                _lblVolDst.ForeColor = Theme.Muted;
            }
            else if (_dstInfo == null)
            {
                _lblVolDst.Text = "目标：无法探测";
                _lblVolDst.ForeColor = Theme.Muted;
            }
            else if (_dstInfo.PathExists && _dstInfo.IsDirectory)
            {
                _lblVolDst.Text = "目标：路径是目录";
                _lblVolDst.ForeColor = Theme.Bad;
            }
            else if (_dstInfo.PathExists)
            {
                _lblVolDst.Text = "目标：文件已存在";
                _lblVolDst.ForeColor = Theme.Bad;
            }
            else if (_srcInfo == null || !_srcInfo.PathExists || _srcInfo.IsDirectory)
            {
                _lblVolDst.Text = "目标：待源文件";
                _lblVolDst.ForeColor = Theme.Muted;
            }
            else if (!_dstInfo.SupportsBlockClone)
            {
                _lblVolDst.Text = "目标：不支持块克隆";
                _lblVolDst.ForeColor = Theme.Bad;
            }
            else if (_dstInfo.Serial == _srcInfo.Serial)
            {
                _lblVolDst.Text = string.Format("目标：同卷 · {0}", _dstInfo.FileSystem);
                _lblVolDst.ForeColor = Theme.Good;
            }
            else
            {
                _lblVolDst.Text = "目标：不同卷，块克隆将被拒绝";
                _lblVolDst.ForeColor = Theme.Bad;
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
            int pct = p.TotalBytes > 0 ? (int)(p.BytesDone * 100 / p.TotalBytes) : 100;
            _lblPercent.Text = pct + "%";
            _lblPercent.ForeColor = Theme.Accent;
            _status.ForeColor = Theme.Text;
            _status.Text = string.Format("正在克隆：{0:0.0} / {1:0.0} MB",
                p.BytesDone / 1048576.0, p.TotalBytes / 1048576.0);
        }

        private void Warn(string message, string status)
        {
            UiHooks.ShowMessage(this, message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtLog.Clear();
            AppendLog("校验未通过：" + status);
            _lblPercent.Text = "";
            _lblPercent.ForeColor = Theme.Muted;
            _status.Text = status;
            _status.ForeColor = Theme.Bad;
        }

#if DEBUG
        internal async Task DoCloneAsync()
#else
        private async Task DoCloneAsync()
#endif
        {
            if (_busy) return;
            _lblPercent.Text = "";
            _lblPercent.ForeColor = Theme.Muted;

            string src = _txtSource.Text.Trim().Trim('"');
            string dst = _txtDest.Text.Trim().Trim('"');
            string fullSrc = RefsBlockCloner.NormalizePath(src);
            string fullDst = RefsBlockCloner.NormalizePath(dst);

            if (fullSrc.Length == 0 || Directory.Exists(fullSrc))
            {
                Warn(fullSrc.Length == 0 ? "请选择或输入源文件。" : "源路径是目录，块克隆仅支持文件。",
                    fullSrc.Length == 0 ? "源文件无效。" : "源路径是目录。");
                return;
            }
            if (!File.Exists(fullSrc))
            {
                Warn("源文件不存在。", "源文件不存在。");
                return;
            }
            if (fullDst.Length == 0)
            {
                Warn("请选择或输入目标文件路径。", "请选择目标。");
                return;
            }
            if (Directory.Exists(fullDst))
            {
                Warn("目标路径是目录，请更换目标文件名。", "目标路径是目录。");
                return;
            }
            if (string.Equals(fullSrc, fullDst, StringComparison.OrdinalIgnoreCase))
            {
                Warn("源文件与目标文件不能相同。", "源与目标相同。");
                return;
            }
            if (File.Exists(fullDst))
            {
                Warn("目标文件已存在，块克隆不会覆盖已有文件，请更换目标文件名。", "目标文件已存在。");
                return;
            }

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
            _btnClone.Text = "克隆中…";
            _btnCancel.Enabled = true;
            _lblPercent.Text = "";
            _lblPercent.ForeColor = Theme.Accent;
            _txtLog.Clear();
            AppendLog("源文件：   " + src);
            AppendLog("目标文件： " + dst);
            AppendLog("----------------------------------------");

            _cts = new CancellationTokenSource();
            var progress = new InlineProgress(p =>
            {
#if DEBUG
                var ov = ProgressOverride;
                if (ov != null) ov(p);
                else ApplyProgress(p);
#else
                ApplyProgress(p);
#endif
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
                _lblPercent.Text = "100%";
                _lblPercent.ForeColor = Theme.Good;
                _status.Text = string.Format("克隆完成，用时 {0:0.000} 秒。", sw.Elapsed.TotalSeconds);
                _status.ForeColor = Theme.Good;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                AppendLog("----------------------------------------");
                AppendLog("已取消，半成品目标已自动清理。");
                _lblPercent.Text = "";
                _lblPercent.ForeColor = Theme.Muted;
                _status.Text = "已取消，半成品目标已自动清理。";
                _status.ForeColor = Theme.Muted;
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppendLog("----------------------------------------");
                AppendLog("克隆失败：" + ex.Message);
                _lblPercent.Text = "";
                _lblPercent.ForeColor = Theme.Muted;
                _status.Text = "克隆失败。";
                _status.ForeColor = Theme.Bad;
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

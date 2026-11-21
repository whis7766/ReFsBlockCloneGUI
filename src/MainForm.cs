using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReFsBlockClone
{
    internal static class UiHooks
    {
        public static Func<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon, DialogResult> ShowMessage =
            (owner, text, caption, buttons, icon) => MessageBox.Show(owner, text, caption, buttons, icon);

        public static Func<IWin32Window, string, string> PickOpenFile = (owner, title) =>
        {
            using (var d = new OpenFileDialog { Title = title, CheckFileExists = true, Filter = "所有文件 (*.*)|*.*" })
                return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
        };

        public static Func<IWin32Window, string, string> PickSaveFile = (owner, title) =>
        {
            using (var d = new SaveFileDialog { Title = title, OverwritePrompt = false, CheckPathExists = true, Filter = "所有文件 (*.*)|*.*" })
                return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
        };
    }

    internal sealed class InlineProgress : IProgress<CloneProgress>
    {
        private readonly Action<CloneProgress> _a;
        public InlineProgress(Action<CloneProgress> a) { _a = a; }
        public void Report(CloneProgress v) { _a(v); }
    }

    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(246, 247, 249);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(229, 231, 235);
        public static readonly Color Text = Color.FromArgb(17, 24, 39);
        public static readonly Color Muted = Color.FromArgb(107, 114, 128);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color Good = Color.FromArgb(22, 163, 74);
        public static readonly Color Bad = Color.FromArgb(220, 38, 38);

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void RoundControl(Control c, int radius)
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            using (var p = Round(new Rectangle(0, 0, c.Width, c.Height), radius))
                c.Region = new Region(p);
        }
    }

    internal sealed class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, 10))
            using (var fill = new SolidBrush(Theme.Card))
                e.Graphics.FillPath(fill, path);
            using (var path = Theme.Round(r, 10))
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class FieldPanel : Panel
    {
        public readonly TextBox Field;

        public FieldPanel(TextBox field)
        {
            Field = field;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            field.BorderStyle = BorderStyle.None;
            field.BackColor = Color.FromArgb(243, 244, 246);
            field.ForeColor = Theme.Text;
            field.Font = new Font("Microsoft YaHei UI", 10F);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Controls.Add(field);
            Resize += (s, e) => LayoutField();
            LayoutField();
        }

        private void LayoutField()
        {
            Field.Left = 12;
            Field.Width = Math.Max(10, Width - 24);
            Field.Top = Math.Max(4, (Height - Field.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            using (var fill = new SolidBrush(Color.FromArgb(243, 244, 246)))
                e.Graphics.FillPath(fill, path);
        }
    }

    internal sealed class RoundButton : Button
    {
        public Color FillColor = Theme.Accent;
        public Color HoverColor = Color.FromArgb(29, 78, 216);
        public Color BorderColor = Color.Transparent;
        public int Radius = 7;
        private bool _hover;

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            MouseLeave += (s, e) => { _hover = false; Invalidate(); };
            EnabledChanged += (s, e) => Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Color.FromArgb(229, 231, 235)
                        : _hover ? HoverColor : FillColor;
            using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            using (var brush = new SolidBrush(fill))
                e.Graphics.FillPath(brush, path);
            if (BorderColor != Color.Transparent && Enabled)
                using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
                using (var pen = new Pen(BorderColor))
                    e.Graphics.DrawPath(pen, path);
            Color text = !Enabled ? Color.FromArgb(156, 163, 175) : ForeColor;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
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
        private CheckBox _chkPhysical = new CheckBox { Name = "chkPhysical" };
        private Label _status = new Label { Name = "statusLabel" };
        private PictureBox _logo = new PictureBox { Name = "picLogo" };

        private readonly System.Windows.Forms.Timer _probeTimer = new System.Windows.Forms.Timer { Interval = 300 };
        private int _probeSeq;
        private VolumeInfo _srcInfo, _dstInfo;
        private bool _busy;
        private bool _destAuto;
        private bool _settingDest;
        private CancellationTokenSource _cts;

        internal static Action<CloneProgress> ProgressOverride;

        internal string LogText { get { return _txtLog.Text; } }
        internal string StatusText { get { return _status.Text; } }

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

        private Label Caption(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                ForeColor = Theme.Muted,
                Margin = new Padding(0, 0, 0, 0)
            };
        }

        private void StyleField(TextBox t)
        {
            t.Margin = new Padding(0);
        }

        private Button GhostButton(string name, string text)
        {
            var b = new RoundButton
            {
                Name = name,
                Text = text,
                Dock = DockStyle.Fill,
                BackColor = Theme.Card,
                Font = new Font("Microsoft YaHei UI", 9F),
                FillColor = Color.White,
                HoverColor = Color.FromArgb(249, 250, 251),
                BorderColor = Color.FromArgb(224, 227, 232),
                ForeColor = Color.FromArgb(55, 65, 81),
                Margin = new Padding(0, 7, 0, 7)
            };
            return b;
        }

        private Button PrimaryButton(string name, string text)
        {
            var b = new RoundButton
            {
                Name = name,
                Text = text,
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                FillColor = Theme.Accent,
                HoverColor = Color.FromArgb(29, 78, 216),
                ForeColor = Color.White,
                Margin = new Padding(0, 6, 0, 6)
            };
            return b;
        }

        private Card FileCard(string captionText, TextBox field, Button browse, Control extraTitle)
        {
            var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 10), BackColor = Theme.Bg };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Card };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var titleHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Margin = new Padding(0) };
            var cap = Caption(captionText);
            cap.Location = new Point(0, 2);
            titleHost.Controls.Add(cap);
            if (extraTitle != null)
            {
                extraTitle.Dock = DockStyle.Right;
                titleHost.Controls.Add(extraTitle);
            }
            grid.Controls.Add(titleHost, 0, 0);
            grid.SetColumnSpan(titleHost, 2);

            var host = new FieldPanel(field) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), BackColor = Theme.Card };
            grid.Controls.Add(host, 0, 1);
            grid.Controls.Add(browse, 1, 1);
            card.Controls.Add(grid);
            return card;
        }

        private void BuildUi()
        {
            Text = "ReFS 块克隆";
            ClientSize = new Size(832, 636);
            MinimumSize = new Size(736, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            DoubleBuffered = true;
            KeyPreview = true;
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && _busy) CancelClone(); };

            _logo.Size = new Size(42, 42);
            _logo.Location = new Point(0, 6);
            _logo.SizeMode = PictureBoxSizeMode.Zoom;
            _logo.BackColor = Color.Transparent;
            if (AppIcon != null) _logo.Image = AppIcon.ToBitmap();

            var title = new Label
            {
                AutoSize = true,
                Text = "ReFS 块克隆",
                Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
                ForeColor = Theme.Text,
                Location = new Point(54, 4)
            };
            var subtitle = new Label
            {
                AutoSize = true,
                Text = "共享物理簇 · 瞬时副本 · 写入接近零",
                Font = new Font("Microsoft YaHei UI", 8.5F),
                ForeColor = Theme.Muted,
                Location = new Point(56, 32)
            };

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            header.Controls.Add(_logo);
            header.Controls.Add(title);
            header.Controls.Add(subtitle);

            StyleField(_txtSource);
            StyleField(_txtDest);
            _txtSource.AllowDrop = true;
            _txtSource.DragEnter += OnDragEnter;
            _txtSource.DragDrop += OnDragDrop;

            var btnBrowseSource = GhostButton("btnBrowseSource", "浏览");
            btnBrowseSource.Click += (s, e) =>
            {
                string p = UiHooks.PickOpenFile(this, "选择源文件");
                if (!string.IsNullOrEmpty(p)) _txtSource.Text = p;
            };

            var btnBrowseDest = GhostButton("btnBrowseDest", "另存为");
            btnBrowseDest.Click += (s, e) =>
            {
                string p = UiHooks.PickSaveFile(this, "选择目标路径");
                if (!string.IsNullOrEmpty(p)) { _destAuto = false; _txtDest.Text = p; }
            };

            _chkPhysical.Text = "物理复制（跨卷可用）";
            _chkPhysical.AutoSize = true;
            _chkPhysical.Font = new Font("Microsoft YaHei UI", 8.5F);
            _chkPhysical.ForeColor = Theme.Muted;
            _chkPhysical.BackColor = Theme.Card;
            _chkPhysical.Cursor = Cursors.Hand;
            _chkPhysical.Margin = new Padding(0);
            _chkPhysical.CheckedChanged += (s, e) => KickProbe();

            var cardSrc = FileCard("源文件", _txtSource, btnBrowseSource, null);
            var cardDst = FileCard("目标文件 · 同一 ReFS 卷上的新路径", _txtDest, btnBrowseDest, _chkPhysical);

            _lblVolSrc.AutoSize = false;
            _lblVolSrc.Dock = DockStyle.Fill;
            _lblVolSrc.TextAlign = ContentAlignment.MiddleLeft;
            _lblVolSrc.Font = new Font("Microsoft YaHei UI", 8.5F);
            _lblVolSrc.ForeColor = Theme.Muted;
            _lblVolSrc.Text = "源文件：待探测";

            _lblVolDst.AutoSize = false;
            _lblVolDst.Dock = DockStyle.Fill;
            _lblVolDst.TextAlign = ContentAlignment.MiddleLeft;
            _lblVolDst.Font = new Font("Microsoft YaHei UI", 8.5F);
            _lblVolDst.ForeColor = Theme.Muted;
            _lblVolDst.Text = "目标：待探测";

            var meta = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg };
            meta.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            meta.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            meta.Controls.Add(_lblVolSrc, 0, 0);
            meta.Controls.Add(_lblVolDst, 0, 1);

            _btnClone = PrimaryButton("btnClone", "开始克隆");
            _btnClone.Enabled = false;
            _btnClone.Click += async (s, e) => await DoCloneAsync();

            _btnCancel = GhostButton("btnCancel", "取消");
            _btnCancel.BackColor = Theme.Bg;
            _btnCancel.Enabled = false;
            _btnCancel.Click += (s, e) => CancelClone();

            _lblPercent.AutoSize = false;
            _lblPercent.Dock = DockStyle.Fill;
            _lblPercent.TextAlign = ContentAlignment.MiddleRight;
            _lblPercent.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            _lblPercent.ForeColor = Theme.Muted;
            _lblPercent.Text = "0%";

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, BackColor = Theme.Bg };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104F));
            actions.Controls.Add(_btnClone, 0, 0);
            actions.Controls.Add(_btnCancel, 1, 0);
            actions.Controls.Add(_lblPercent, 3, 0);

            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.BorderStyle = BorderStyle.None;
            _txtLog.Font = new Font("Consolas", 9F);
            _txtLog.BackColor = Theme.Card;
            _txtLog.ForeColor = Color.FromArgb(55, 65, 81);
            _txtLog.Margin = new Padding(0);

            var logCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 12) };
            var logGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Card };
            logGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            logGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var logCap = Caption("日志");
            logCap.Dock = DockStyle.Fill;
            logGrid.Controls.Add(logCap, 0, 0);
            logGrid.Controls.Add(_txtLog, 0, 1);
            logCard.Controls.Add(logGrid);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(18, 12, 18, 10), BackColor = Theme.Bg };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(cardSrc, 0, 1);
            root.Controls.Add(cardDst, 0, 2);
            root.Controls.Add(meta, 0, 3);
            root.Controls.Add(actions, 0, 4);
            root.Controls.Add(logCard, 0, 5);

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Color.White, Padding = new Padding(18, 0, 18, 0) };
            bar.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Border))
                    e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
            };
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.AutoEllipsis = true;
            _status.Font = new Font("Microsoft YaHei UI", 8.5F);
            _status.ForeColor = Theme.Muted;
            _status.Text = "就绪，选择源文件与目标路径后开始克隆。";
            bar.Controls.Add(_status);

            Controls.Add(root);
            Controls.Add(bar);

            _txtSource.TextChanged += (s, e) => { SuggestDest(); KickProbe(); UpdateCloneEnabled(); };
            _txtDest.TextChanged += (s, e) => { if (!_settingDest) _destAuto = false; KickProbe(); UpdateCloneEnabled(); };
            AcceptButton = _btnClone;
        }

        private void BrowseSource()
        {
            string p = UiHooks.PickOpenFile(this, "选择源文件");
            if (!string.IsNullOrEmpty(p)) _txtSource.Text = p;
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
                _lblVolSrc.Text = "源文件：无法探测（路径不存在或不可访问）";
                _lblVolSrc.ForeColor = Theme.Bad;
            }
            else
            {
                _lblVolSrc.Text = string.Format("源文件：{0} · 簇 {1} B · 完整性 {2} · 稀疏 {3}",
                    _srcInfo.FileSystem, _srcInfo.ClusterSize,
                    _srcInfo.IntegrityEnabled ? "开" : "关", _srcInfo.IsSparse ? "是" : "否");
                _lblVolSrc.ForeColor = _srcInfo.SupportsBlockClone ? Theme.Muted : Theme.Bad;
            }

            if (_dstInfo == null)
            {
                _lblVolDst.Text = "目标：待探测";
                _lblVolDst.ForeColor = Theme.Muted;
            }
            else if (_srcInfo != null && _dstInfo.Serial == _srcInfo.Serial && _dstInfo.SupportsBlockClone)
            {
                _lblVolDst.Text = string.Format("目标：与源同卷 ✓（{0}{1}）",
                    _dstInfo.FileSystem, _chkPhysical.Checked ? "；物理复制模式" : "，可块克隆");
                _lblVolDst.ForeColor = Theme.Good;
            }
            else if (_chkPhysical.Checked)
            {
                _lblVolDst.Text = string.Format("目标：{0}（物理复制可用）", _dstInfo.FileSystem);
                _lblVolDst.ForeColor = Theme.Muted;
            }
            else
            {
                _lblVolDst.Text = string.Format("目标：{0} —— 与源不同卷，块克隆将被拒绝", _dstInfo.FileSystem);
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

            bool physical = _chkPhysical.Checked;
            bool crossVolume;
            if (_srcInfo != null && _dstInfo != null)
                crossVolume = _srcInfo.Serial != _dstInfo.Serial;
            else
            {
                string sr = Path.GetPathRoot(src), dr = Path.GetPathRoot(dst);
                crossVolume = !string.IsNullOrEmpty(sr) && !string.IsNullOrEmpty(dr) &&
                    !string.Equals(sr, dr, StringComparison.OrdinalIgnoreCase);
            }
            if (crossVolume && !physical)
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
            _lblPercent.Text = "0%";
            _lblPercent.ForeColor = Theme.Accent;
            _txtLog.Clear();
            AppendLog("源文件：   " + src);
            AppendLog("目标文件： " + dst);
            AppendLog("模式：     " + (physical ? "物理复制（不共享块）" : "块克隆（共享物理簇）"));
            AppendLog("----------------------------------------");

            _cts = new CancellationTokenSource();
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
                    if (physical) cloner.ClonePhysical(src, dst, progress, _cts.Token);
                    else cloner.Clone(src, dst, progress, _cts.Token);
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

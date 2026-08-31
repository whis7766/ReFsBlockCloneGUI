using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReFsBlockClone
{
    public sealed class MainForm : Form
    {
        private readonly TextBox _txtSource = new TextBox();
        private readonly TextBox _txtDest = new TextBox();
        private readonly Button _btnClone = new Button();
        private readonly TextBox _txtLog = new TextBox();
        private bool _busy;

        public MainForm()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "ReFS 块克隆";
            MinimumSize = new Size(640, 460);
            Size = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            AcceptButton = _btnClone;

            // Source row
            var lblSource = new Label
            {
                Text = "源文件（ReFS 卷上的原文件，可输入或浏览）：",
                AutoSize = true
            };
            _txtSource.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            var btnBrowseSource = new Button { Text = "浏览...", Width = 90 };
            btnBrowseSource.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog
                {
                    Title = "选择要克隆的源文件",
                    CheckFileExists = true,
                    Filter = "所有文件 (*.*)|*.*"
                })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) _txtSource.Text = dlg.FileName;
                }
            };

            // Destination row
            var lblDest = new Label
            {
                Text = "克隆到（目标路径与文件名，可输入或选择）：",
                AutoSize = true
            };
            _txtDest.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            var btnBrowseDest = new Button { Text = "另存为...", Width = 90 };
            btnBrowseDest.Click += (s, e) =>
            {
                using (var dlg = new SaveFileDialog
                {
                    Title = "选择目标文件位置与名称",
                    OverwritePrompt = false, // block clone never overwrites; checked in code
                    CheckPathExists = true,
                    Filter = "所有文件 (*.*)|*.*"
                })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) _txtDest.Text = dlg.FileName;
                }
            };

            // Clone button, enabled only when both paths are filled
            _btnClone.Text = "开始克隆";
            _btnClone.Enabled = false;
            _btnClone.Click += async (s, e) => await DoCloneAsync();
            _txtSource.TextChanged += (s, e) => UpdateCloneEnabled();
            _txtDest.TextChanged += (s, e) => UpdateCloneEnabled();

            // Log area
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.Dock = DockStyle.Fill;

            // Layout: two-column grid (content + button)
            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Padding = new Padding(10)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            tbl.Controls.Add(lblSource, 0, 0);
            tbl.Controls.Add(_txtSource, 0, 1);
            tbl.Controls.Add(btnBrowseSource, 1, 1);
            tbl.Controls.Add(lblDest, 0, 2);
            tbl.Controls.Add(_txtDest, 0, 3);
            tbl.Controls.Add(btnBrowseDest, 1, 3);
            tbl.Controls.Add(_btnClone, 0, 4);
            tbl.Controls.Add(_txtLog, 0, 5);
            tbl.SetColumnSpan(_txtLog, 2);

            Controls.Add(tbl);
        }

        private void UpdateCloneEnabled()
        {
            _btnClone.Enabled = !_busy && _txtSource.Text.Trim().Length > 0 && _txtDest.Text.Trim().Length > 0;
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

        private async Task DoCloneAsync()
        {
            if (_busy) return;

            string src = _txtSource.Text.Trim().Trim('"');
            string dst = _txtDest.Text.Trim().Trim('"');

            if (string.IsNullOrEmpty(src) || !File.Exists(src))
            {
                MessageBox.Show(this, "请选择或输入一个已存在的源文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(dst))
            {
                MessageBox.Show(this, "请选择或输入目标文件路径与名称。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                string fullSrc = Path.GetFullPath(src);
                string fullDst = Path.GetFullPath(dst);

                if (string.Equals(fullSrc, fullDst, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "源文件与目标文件不能相同。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (File.Exists(fullDst))
                {
                    MessageBox.Show(this, "目标文件已存在。\n\n块克隆不会覆盖已有文件，请更换目标文件名。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Warn when source and destination are on different drives; block
                // clone requires the same ReFS volume.
                string srcRoot = Path.GetPathRoot(fullSrc);
                string dstRoot = Path.GetPathRoot(fullDst);
                if (!string.IsNullOrEmpty(srcRoot) && !string.IsNullOrEmpty(dstRoot) &&
                    !string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (MessageBox.Show(this,
                        "源文件与目标文件在不同盘符（" + srcRoot + " 与 " + dstRoot + "）。\n" +
                        "块克隆要求源与目标位于同一 ReFS 卷，否则会失败。仍要继续吗？",
                        "跨卷提示", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                }
            }
            catch { }

            _busy = true;
            UpdateCloneEnabled();
            _btnClone.Text = "正在克隆...";
            _txtLog.Clear();
            AppendLog("源文件   : " + src);
            AppendLog("目标文件 : " + dst);
            AppendLog("----------------------------------------");

            var sw = Stopwatch.StartNew();
            try
            {
                await Task.Run(() =>
                {
                    var cloner = new RefsBlockCloner(AppendLog);
                    cloner.Clone(src, dst);
                });
                sw.Stop();
                AppendLog("----------------------------------------");
                AppendLog(string.Format("克隆完成，用时 {0:0.000} 秒。", sw.Elapsed.TotalSeconds));
                if (!IsDisposed)
                    MessageBox.Show(this, "克隆完成。\n" + dst, "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppendLog("----------------------------------------");
                AppendLog("失败：" + ex.Message);
                if (!IsDisposed)
                    MessageBox.Show(this, "克隆失败：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                _btnClone.Text = "开始克隆";
                UpdateCloneEnabled();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FbwgModManager
{
    public class MainForm : Form
    {
        private readonly Queue<Action> _uiQueue = new Queue<Action>();
        private readonly object _queueLock = new object();
        private readonly System.Windows.Forms.Timer _uiTimer;

        private Dictionary<string, InstallInfo> _installed =
            new Dictionary<string, InstallInfo>(StringComparer.OrdinalIgnoreCase);
        private string _selected = "forest";
        private volatile bool _busy;

        private ListView _list;
        private Button _btnScan, _btnInstallAll, _btnUninstallAll;
        private Label _summary;
        private GroupBox _detailBox;
        private Label _detailTitle, _hint;
        private Button _btnInstall, _btnUninstall, _btnOpenDir, _btnCopy;
        private TextBox _log;

        public MainForm()
        {
            Text = "Fireboy & Watergirl Mod 部署管理器";
            ClientSize = new Size(1080, 620);
            MinimumSize = new Size(860, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Font = new Font("Microsoft YaHei UI", 9F); }
            catch { Font = SystemFonts.MessageBoxFont; }

            BuildUi();

            _uiTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _uiTimer.Tick += (s, e) => DrainQueue();
            _uiTimer.Start();

            Shown += (s, e) => Scan();
        }

        // ── 界面构建 ────────────────────────────────────────────────────────
        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));   // 工具栏
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));  // 列表
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));  // 详情
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 日志

            // --- 工具栏 ---
            var toolbar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 6) };

            _btnScan = new Button
            {
                Text = "重新探测 Steam 并扫描游戏",
                AutoSize = true,
                Location = new Point(8, 6),
            };
            _btnScan.Click += (s, e) => Scan();
            toolbar.Controls.Add(_btnScan);

            _btnInstallAll = new Button
            {
                Text = "全部安装",
                AutoSize = true,
                Location = new Point(_btnScan.Right + 16, 6),
            };
            _btnInstallAll.Click += (s, e) => InstallAll();
            toolbar.Controls.Add(_btnInstallAll);

            _btnUninstallAll = new Button
            {
                Text = "全部卸载",
                AutoSize = true,
                Location = new Point(_btnInstallAll.Right + 4, 6),
            };
            _btnUninstallAll.Click += (s, e) => UninstallAll();
            toolbar.Controls.Add(_btnUninstallAll);

            _summary = new Label { AutoSize = true, TextAlign = ContentAlignment.MiddleRight };
            toolbar.Controls.Add(_summary);
            toolbar.Resize += (s, e) =>
            {
                _summary.Location = new Point(
                    toolbar.ClientSize.Width - _summary.Width - 12,
                    (toolbar.ClientSize.Height - _summary.Height) / 2);
            };

            // --- 列表 ---
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            _list.Columns.Add("游戏", 180);
            _list.Columns.Add("appid", 90, HorizontalAlignment.Center);
            _list.Columns.Add("安装路径", 760);
            _list.SelectedIndexChanged += (s, e) => OnSelect();

            foreach (var g in GameCatalog.Games)
            {
                var item = new ListViewItem(new[] { g.Name, "-", "未扫描" })
                {
                    Name = g.Key,
                    Tag = g.Key,
                };
                _list.Items.Add(item);
            }

            // --- 详情 ---
            _detailBox = new GroupBox
            {
                Text = "选中游戏的操作",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
            };

            _detailTitle = new Label
            {
                AutoSize = true,
                Font = new Font(this.Font, FontStyle.Bold),
                Location = new Point(12, 24),
            };
            _detailBox.Controls.Add(_detailTitle);

            _btnInstall = new Button
            {
                Text = "安装（自动加载）",
                AutoSize = true,
                Location = new Point(12, 52),
            };
            _btnInstall.Click += (s, e) => InstallSelected();
            _detailBox.Controls.Add(_btnInstall);

            _btnUninstall = new Button
            {
                Text = "卸载",
                AutoSize = true,
                Location = new Point(_btnInstall.Right + 6, 52),
            };
            _btnUninstall.Click += (s, e) => UninstallSelected();
            _detailBox.Controls.Add(_btnUninstall);

            _btnOpenDir = new Button
            {
                Text = "打开游戏目录",
                AutoSize = true,
                Location = new Point(_btnUninstall.Right + 6, 52),
            };
            _btnOpenDir.Click += (s, e) => OpenDir();
            _detailBox.Controls.Add(_btnOpenDir);

            _btnCopy = new Button
            {
                Text = "复制脚本到剪贴板",
                AutoSize = true,
                Location = new Point(_btnOpenDir.Right + 6, 52),
            };
            _btnCopy.Click += (s, e) => CopyScript();
            _detailBox.Controls.Add(_btnCopy);

            _hint = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(80, 80, 80),
                Location = new Point(12, 86),
                Size = new Size(1000, 56),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            _detailBox.Controls.Add(_hint);

            // --- 日志 ---
            var logBox = new GroupBox
            {
                Text = "日志",
                Dock = DockStyle.Fill,
                Padding = new Padding(4),
            };
            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                WordWrap = true,
                Font = new Font("Consolas", 9F),
                BackColor = Color.White,
            };
            logBox.Controls.Add(_log);

            root.Controls.Add(toolbar, 0, 0);
            root.Controls.Add(_list, 0, 1);
            root.Controls.Add(_detailBox, 0, 2);
            root.Controls.Add(logBox, 0, 3);

            Controls.Add(root);
        }

        // ── 工具 ────────────────────────────────────────────────────────────
        private void LogLine(string msg)
        {
            if (_log.IsDisposed) return;
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
        }

        private void EnqueueUi(Action a)
        {
            lock (_queueLock) _uiQueue.Enqueue(a);
        }

        private void DrainQueue()
        {
            while (true)
            {
                Action a;
                lock (_queueLock)
                {
                    if (_uiQueue.Count == 0) break;
                    a = _uiQueue.Dequeue();
                }
                try { a(); } catch { /* 与 tkinter 版一致：吞掉 UI 异常 */ }
            }
        }

        private GameDef CurrentGame()
        {
            return GameCatalog.Get(_selected);
        }

        private void RunBackground(Action work)
        {
            if (_busy)
            {
                LogLine("上一项操作还在进行，已忽略本次点击");
                return;
            }
            _busy = true;
            var t = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    EnqueueUi(() => LogLine("错误：" + msg));
                }
                finally { _busy = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ── 扫描 ────────────────────────────────────────────────────────────
        private void Scan()
        {
            RunBackground(() =>
            {
                var d = SteamFinder.Discover();
                foreach (var n in d.Notes)
                {
                    var l = n;
                    EnqueueUi(() => LogLine(l));
                }
                if (d.Libraries.Count > 0)
                {
                    var count = d.Libraries.Count;
                    var joined = string.Join(" ; ", d.Libraries.ToArray());
                    EnqueueUi(() => LogLine("游戏库（" + count + " 个）：" + joined));
                }

                var found = SteamFinder.FindInstalled(d.Libraries);
                EnqueueUi(() => ApplyScan(found));
            });
        }

        private void ApplyScan(Dictionary<string, InstallInfo> found)
        {
            _installed = found;

            foreach (var g in GameCatalog.Games)
            {
                InstallInfo info;
                found.TryGetValue(g.Key, out info);

                var item = _list.Items[g.Key];
                if (item == null) continue;
                item.SubItems[1].Text = info != null ? (info.AppId ?? "-") : "-";
                item.SubItems[2].Text = info != null ? info.Path : "未安装";
            }

            _summary.Text = "找到 " + found.Count + " / 7 作";
            LogLine("扫描完成：找到 " + found.Count + " 作");

            if (_list.SelectedItems.Count > 0) OnSelect();
        }

        // ── 选中 ────────────────────────────────────────────────────────────
        private void OnSelect()
        {
            if (_list.SelectedItems.Count == 0) return;
            _selected = (string)_list.SelectedItems[0].Tag;
            var g = CurrentGame();
            if (g == null) return;

            _detailTitle.Text = g.Name;

            InstallInfo info;
            if (!_installed.TryGetValue(_selected, out info))
            {
                _hint.Text = "未安装（未在 Steam 库中找到）";
                return;
            }

            var www = GameModder.WwwOf(info.Path);
            _hint.Text =
                "安装路径：" + info.Path + Environment.NewLine +
                "将写入：" + Path.Combine(www, "js", GameModder.ModBasename) + Environment.NewLine +
                "将修改：" + Path.Combine(www, "index.html") + "（插入/移除一行）";
        }

        private InstallInfo NeedSelected()
        {
            InstallInfo info;
            if (!_installed.TryGetValue(_selected, out info))
            {
                MessageBox.Show(this,
                    "「" + CurrentGame().Name + "」未在 Steam 库中找到。",
                    "未安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            return info;
        }

        // ── 单作操作 ────────────────────────────────────────────────────────
        private void InstallSelected()
        {
            if (NeedSelected() != null)
                RunBackground(() => DoInstall(new[] { _selected }));
        }

        private void UninstallSelected()
        {
            if (NeedSelected() != null)
                RunBackground(() => DoUninstall(new[] { _selected }));
        }

        private void DoInstall(string[] keys)
        {
            foreach (var k in keys)
            {
                var g = GameCatalog.Get(k);
                List<string> lines;
                try { lines = GameModder.Install(g, _installed[k]); }
                catch (Exception ex) { lines = new List<string> { "错误：" + ex.Message }; }

                var gName = g.Name;
                foreach (var line in lines)
                {
                    var l = line;
                    EnqueueUi(() => LogLine(gName + "：" + l));
                }
            }
        }

        private void DoUninstall(string[] keys)
        {
            foreach (var k in keys)
            {
                var g = GameCatalog.Get(k);
                List<string> lines;
                try { lines = GameModder.Uninstall(g, _installed[k]); }
                catch (Exception ex) { lines = new List<string> { "错误：" + ex.Message }; }

                var gName = g.Name;
                foreach (var line in lines)
                {
                    var l = line;
                    EnqueueUi(() => LogLine(gName + "：" + l));
                }
            }
        }

        // ── 批量操作 ────────────────────────────────────────────────────────
        private void InstallAll()
        {
            var keys = new List<string>(_installed.Keys).ToArray();
            if (keys.Length == 0)
            {
                MessageBox.Show(this, "扫描结果为空。", "没有可安装的游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RunBackground(() => DoInstall(keys));
        }

        private void UninstallAll()
        {
            var keys = new List<string>(_installed.Keys).ToArray();
            if (keys.Length == 0)
            {
                MessageBox.Show(this, "扫描结果为空。", "没有可卸载的游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var answer = MessageBox.Show(this,
                "将对 " + keys.Length + " 作执行：删除 zzmod.js 并移除 index.html 里那一行。\n继续？",
                "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            RunBackground(() => DoUninstall(keys));
        }

        // ── 辅助 ────────────────────────────────────────────────────────────
        private void OpenDir()
        {
            InstallInfo info;
            if (_installed.TryGetValue(_selected, out info) && Directory.Exists(info.Path))
            {
                try { Process.Start("explorer.exe", "\"" + info.Path + "\""); }
                catch (Exception ex) { LogLine("打开失败：" + ex.Message); }
            }
        }

        private void CopyScript()
        {
            try
            {
                Clipboard.SetText(ModScript.MOD_JS);
                LogLine("已把内置 mod 代码（" + ModScript.MOD_JS.Length +
                        " 字符）复制到剪贴板，可在游戏控制台里直接粘贴");
            }
            catch (Exception ex)
            {
                LogLine("复制失败：" + ex.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_uiTimer != null) { _uiTimer.Stop(); _uiTimer.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
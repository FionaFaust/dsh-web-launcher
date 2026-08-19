using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshWebLauncher
{
    /// <summary>
    /// 主窗体（ver1.1.5.0 重构）：保留原版全部 GUI 特性
    /// （120fps 滑动动画 / F11-Esc 全局热键 / 跟随窗口+固定分辨率双模 / 状态栏），
    /// 启动流程改用 ServiceStarter（自动启动 + 三级兜底 + 超时重试 + 健康监控）。
    /// </summary>
    internal sealed class BrowserForm : Form
    {
        // 全局热键：F11 全屏 / Esc 退出
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_F11 = 0x7A;
        private const uint VK_ESCAPE = 0x1B;
        private const int HOTKEY_ID_F11 = 1;
        private const int HOTKEY_ID_ESC = 2;
        private bool escHotkeyRegistered = false;

        private WebView2 webView;
        private Panel hostPanel;
        private ToolStrip toolStrip;
        private ToolStripButton btnBack, btnForward, btnRefresh, btnHome;
        private ToolStripComboBox cmbResolution;
        private ToolStripTextBox txtUrl;
        private ToolStripStatusLabel statusLabel;
        private bool initialized = false;
        private Size renderSize = new Size(3840, 2160);
        private bool isFullscreen = false;
        private FormBorderStyle savedBorder;
        private Rectangle savedBounds;
        private bool savedToolStripVisible, savedStatusVisible;
        private System.Windows.Forms.Timer hoverTimer;
        private System.Threading.Timer slideTimer;
        private Stopwatch slideClock = Stopwatch.StartNew();
        private double slideStartY, slideEndY, slideStartMs;
        private int toolbarTargetY = int.MinValue;
        private const int HoverZoneHeight = 4;
        private const double SlideDurationMs = 260;
        private object slideLock = new object();

        private readonly ServiceStarter _svc;
        private bool _failedShown;      // 启动失败弹窗只弹一次

        public BrowserForm()
        {
            LauncherConfig cfg = Program.Config;
            Text = cfg.Title;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(960, 640);

            // 图标：优先 exe 旁 app.ico，否则用黑色鲸鱼图标
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string extIco = Path.Combine(dir, "app.ico");
                if (File.Exists(extIco))
                {
                    using (Icon i = new Icon(extIco)) Icon = (Icon)i.Clone();
                }
                else
                {
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("whale.ico"))
                    {
                        if (s != null) Icon = new Icon(s);
                    }
                }
            }
            catch { }

            // 初始窗口 = 屏幕 × windowScale
            Size screen = Screen.PrimaryScreen.Bounds.Size;
            double scale = cfg.WindowScale;
            if (scale <= 0.1 || scale > 1.0) scale = 0.72;
            int w = (int)(screen.Width * scale);
            int h = (int)(screen.Height * scale);
            if (w < 1100) w = 1100;
            if (h < 700) h = 700;
            ClientSize = new Size(w, h);

            BuildToolStrip();
            BuildHostPanel();
            BuildWebView();
            BuildStatusStrip();

            // 顶部导航条自动隐藏 + 滑动动画
            if (cfg.AutoHideToolbar)
            {
                toolStrip.Dock = DockStyle.None;
                toolStrip.Visible = false;
                timeBeginPeriod(1);
                slideTimer = new System.Threading.Timer(OnSlideTick, null,
                    System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                hoverTimer = new System.Windows.Forms.Timer();
                hoverTimer.Interval = 150;
                hoverTimer.Tick += OnHoverTick;
                hoverTimer.Start();
            }

            // 服务启动器：构造时注入，监听状态变化
            _svc = new ServiceStarter(cfg);

            Shown += OnFirstShown;
            Resize += OnWindowResize;
            Load += OnFormLoad;
            FormClosing += OnFormClosing;
            HandleCreated += (s2, e2) => RegisterHotKey(Handle, HOTKEY_ID_F11, MOD_NOREPEAT, VK_F11);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (IsForegroundApp())
                {
                    if (id == HOTKEY_ID_F11) { ToggleFullscreen(!isFullscreen); return; }
                    if (id == HOTKEY_ID_ESC && isFullscreen) { ToggleFullscreen(false); return; }
                }
            }
            base.WndProc(ref m);
        }

        private bool IsForegroundApp()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == Handle) return true;
            return IsChild(Handle, fg);
        }

        private void UpdateEscHotkey()
        {
            try
            {
                if (isFullscreen && !escHotkeyRegistered)
                    escHotkeyRegistered = RegisterHotKey(Handle, HOTKEY_ID_ESC, MOD_NOREPEAT, VK_ESCAPE);
                else if (!isFullscreen && escHotkeyRegistered)
                {
                    UnregisterHotKey(Handle, HOTKEY_ID_ESC);
                    escHotkeyRegistered = false;
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        // ---------------- 动画（保留原版） ----------------

        private void OnFirstShown(object sender, EventArgs e)
        {
            toolbarTargetY = -toolStrip.Height;
            toolStrip.Location = new Point(0, toolbarTargetY);
            toolStrip.Visible = false;
        }

        private void OnHoverTick(object sender, EventArgs e)
        {
            if (!IsHandleCreated || !Visible || !Program.Config.AutoHideToolbar) return;
            if (toolbarTargetY == int.MinValue) return;
            Point cursor = PointToClient(Cursor.Position);
            bool overToolbar = toolStrip.Bounds.Contains(cursor);
            bool nearTop = cursor.Y >= 0 && cursor.Y <= HoverZoneHeight;
            bool show = overToolbar || nearTop;
            int target = show ? 0 : -toolStrip.Height;
            if (target != toolbarTargetY)
            {
                toolbarTargetY = target;
                if (show) { toolStrip.Visible = true; toolStrip.BringToFront(); }
                StartSlide(target);
            }
        }

        private void StartSlide(int targetY)
        {
            slideStartY = toolStrip.Top;
            slideEndY = targetY;
            slideStartMs = slideClock.Elapsed.TotalMilliseconds;
            slideTimer.Change(0, 8);
        }

        private void OnSlideTick(object state)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(AnimateSlideFrame));
            }
            catch { }
        }

        private void AnimateSlideFrame()
        {
            if (IsDisposed) return;
            double t = (slideClock.Elapsed.TotalMilliseconds - slideStartMs) / SlideDurationMs;
            if (t >= 1.0)
            {
                toolStrip.Location = new Point(0, (int)slideEndY);
                slideTimer.Change(Timeout.Infinite, Timeout.Infinite);
                if (slideEndY < 0) toolStrip.Visible = false;
                return;
            }
            double ease = 1 - Math.Pow(1 - t, 3);
            int y = (int)(slideStartY + (slideEndY - slideStartY) * ease);
            toolStrip.Location = new Point(0, y);
        }

        private void SyncToolbar()
        {
            if (toolStrip == null) return;
            toolStrip.Width = ClientSize.Width;
            if (toolbarTargetY == int.MinValue) return;
            toolStrip.Location = new Point(0, toolbarTargetY);
        }

        private void ToggleFullscreen(bool enter)
        {
            if (enter == isFullscreen) return;
            if (enter)
            {
                savedBorder = FormBorderStyle;
                savedBounds = Bounds;
                savedToolStripVisible = toolStrip.Visible;
                savedStatusVisible = statusLabel.Owner.Visible;

                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                if (toolbarTargetY != int.MinValue)
                {
                    toolbarTargetY = -toolStrip.Height;
                    toolStrip.Visible = false;
                    slideTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    toolStrip.Location = new Point(0, toolbarTargetY);
                }
                statusLabel.Owner.Visible = false;
                Bounds = Screen.FromHandle(Handle).Bounds;
                isFullscreen = true;
                UpdateEscHotkey();
                LayoutWebView();
            }
            else
            {
                FormBorderStyle = savedBorder;
                statusLabel.Owner.Visible = savedStatusVisible;
                Bounds = savedBounds;
                isFullscreen = false;
                UpdateEscHotkey();
                LayoutWebView();
            }
        }

        // ---------------- 分辨率双模（保留原版） ----------------

        private bool IsFixedResolution()
        {
            return cmbResolution != null && cmbResolution.SelectedIndex > 0;
        }

        private Size CurrentRenderSize()
        {
            if (cmbResolution != null && cmbResolution.SelectedIndex > 0)
                return Program.Config.Resolutions[cmbResolution.SelectedIndex - 1];
            return hostPanel == null ? new Size(1920, 1080) : hostPanel.ClientSize;
        }

        private void BuildToolStrip()
        {
            toolStrip = new ToolStrip();
            toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip.Padding = new Padding(6, 2, 6, 2);

            btnBack = new ToolStripButton("后退") { ToolTipText = "后退" };
            btnForward = new ToolStripButton("前进") { ToolTipText = "前进" };
            btnRefresh = new ToolStripButton("刷新") { ToolTipText = "刷新" };
            btnHome = new ToolStripButton("主页") { ToolTipText = "主页" };
            btnBack.Click += (s, e) => { if (initialized && webView.CanGoBack) webView.GoBack(); };
            btnForward.Click += (s, e) => { if (initialized && webView.CanGoForward) webView.GoForward(); };
            btnRefresh.Click += (s, e) => { if (initialized) webView.Reload(); };
            btnHome.Click += (s, e) => { if (initialized) webView.CoreWebView2.Navigate(Program.Config.HomeUrl); };

            cmbResolution = new ToolStripComboBox();
            cmbResolution.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbResolution.Items.Add("跟随窗口（推荐）");
            foreach (Size r in Program.Config.Resolutions)
                cmbResolution.Items.Add("固定 " + r.Width + " × " + r.Height);
            cmbResolution.SelectedIndex = 0;
            cmbResolution.Width = 170;
            cmbResolution.ToolTipText = "跟随窗口：清晰渲染；固定档位：锁定渲染分辨率";
            cmbResolution.SelectedIndexChanged += OnResolutionChanged;

            txtUrl = new ToolStripTextBox();
            txtUrl.Width = 360;
            txtUrl.Enabled = false;
            txtUrl.Font = new Font("Microsoft YaHei UI", 9F);

            toolStrip.Items.Add(btnBack);
            toolStrip.Items.Add(btnForward);
            toolStrip.Items.Add(btnRefresh);
            toolStrip.Items.Add(btnHome);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(new ToolStripLabel("分辨率:"));
            toolStrip.Items.Add(cmbResolution);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(txtUrl);

            Controls.Add(toolStrip);
        }

        private void BuildHostPanel()
        {
            hostPanel = new Panel();
            hostPanel.Dock = DockStyle.Fill;
            hostPanel.BackColor = Color.Black;
            Controls.Add(hostPanel);
        }

        private void BuildWebView()
        {
            webView = new WebView2();
            webView.BackColor = Color.Black;
            hostPanel.Controls.Add(webView);
        }

        private void BuildStatusStrip()
        {
            StatusStrip strip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("少女祈祷中...");
            strip.Items.Add(statusLabel);
            ToolStripStatusLabel brand = new ToolStripStatusLabel("艾珀莉亚出品 · " + Program.Version);
            brand.Alignment = ToolStripItemAlignment.Right;
            strip.Items.Add(brand);
            Controls.Add(strip);
        }

        private void LayoutWebView()
        {
            if (hostPanel == null || webView == null) return;
            int cw = hostPanel.ClientSize.Width;
            int ch = hostPanel.ClientSize.Height;
            if (cw <= 0 || ch <= 0) return;

            if (!IsFixedResolution())
            {
                webView.SetBounds(0, 0, cw, ch);
                return;
            }

            double scale = Math.Min((double)cw / renderSize.Width, (double)ch / renderSize.Height);
            if (scale < 0.05) scale = 0.05;
            if (scale > 4.0) scale = 4.0;
            int vw = (int)Math.Round(renderSize.Width * scale);
            int vh = (int)Math.Round(renderSize.Height * scale);
            int vx = (cw - vw) / 2;
            int vy = (ch - vh) / 2;
            webView.SetBounds(vx, vy, vw, vh);

            if (initialized)
            {
                try
                {
                    CoreWebView2Controller ctl = GetController();
                    if (ctl != null)
                    {
                        ctl.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
                        ctl.ShouldDetectMonitorScaleChanges = false;
                        ctl.RasterizationScale = scale;
                    }
                }
                catch { }
            }
        }

        private void ApplyRenderMode()
        {
            try
            {
                CoreWebView2Controller ctl = GetController();
                if (ctl == null) return;
                if (IsFixedResolution())
                {
                    ctl.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
                    ctl.ShouldDetectMonitorScaleChanges = false;
                }
                else
                {
                    ctl.ShouldDetectMonitorScaleChanges = true;
                }
            }
            catch { }
        }

        private void OnWindowResize(object sender, EventArgs e)
        {
            LayoutWebView();
            SyncToolbar();
        }

        private void OnResolutionChanged(object sender, EventArgs e)
        {
            if (cmbResolution.SelectedIndex < 0) return;
            if (IsFixedResolution())
            {
                renderSize = Program.Config.Resolutions[cmbResolution.SelectedIndex - 1];
            }
            if (initialized)
            {
                ApplyRenderMode();
                LayoutWebView();
                statusLabel.Text = IsFixedResolution()
                    ? "已切换为固定分辨率：" + renderSize.Width + " × " + renderSize.Height
                    : "已切换为跟随窗口模式（清晰渲染）";
            }
        }

        private CoreWebView2Controller GetController()
        {
            if (webView == null) return null;
            System.Reflection.FieldInfo f = typeof(WebView2)
                .GetField("_coreWebView2Controller",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return f == null ? null : f.GetValue(webView) as CoreWebView2Controller;
        }

        // ---------------- 启动流程（ver1.1.5.0：改用 ServiceStarter） ----------------

        private async void OnFormLoad(object sender, EventArgs e)
        {
            toolStrip.BringToFront();
            statusLabel.Text = "少女祈祷中...";

            // 自动启动：探测 → 三级兜底拉起 → 等待就绪 → 失败重试
            bool ok = await System.Threading.Tasks.Task.Run(() => _svc.StartIfNeeded());
            UpdateServiceStatus();

            if (!ok)
            {
                statusLabel.Text = "启动失败：服务未就绪。";
                ShowStartFailure();
                return;
            }

            // 服务就绪后才初始化 WebView
            try
            {
                await webView.EnsureCoreWebView2Async(null);
                initialized = true;
                ApplyRenderMode();
                LayoutWebView();

                webView.CoreWebView2.Navigate(Program.Config.HomeUrl);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

                webView.NavigationStarting += (s2, e2) => { statusLabel.Text = "少女祈祷中..."; };
                webView.NavigationCompleted += async (s2, e2) =>
                {
                    string uri = webView.CoreWebView2.Source;
                    txtUrl.Text = uri;
                    if (e2.IsSuccess)
                    {
                        string js = "window.innerWidth + ' x ' + window.innerHeight + ' @ ' + window.devicePixelRatio";
                        string vp = "";
                        try { vp = await webView.CoreWebView2.ExecuteScriptAsync(js); vp = vp.Trim('"'); }
                        catch { }
                        statusLabel.Text = IsFixedResolution()
                            ? "就绪 · 固定渲染 " + renderSize.Width + " × " + renderSize.Height + " · 视口 " + vp
                            : "就绪 · 跟随窗口 · 视口 " + vp;
                    }
                    else
                    {
                        statusLabel.Text = "加载失败：" + e2.WebErrorStatus;
                    }
                };
                statusLabel.Text = "少女祈祷中...";
            }
            catch (Exception ex)
            {
                statusLabel.Text = "浏览器初始化失败。";
                MessageBox.Show(this, "无法初始化内嵌浏览器：\n" + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>把 ServiceStarter 的实时状态映射到状态栏（监控回调可能跨线程，经 Invoke 投递）。</summary>
        private void UpdateServiceStatus()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(delegate
                {
                    statusLabel.Text = _svc.Detail ?? statusLabel.Text;
                }));
            }
            catch { }
        }

        /// <summary>启动失败：弹一次带完整原因与建议的提示框（比原版信息更可操作）。</summary>
        private void ShowStartFailure()
        {
            if (_failedShown) return;
            _failedShown = true;
            string hint;
            if (_svc.Status == ServiceStatus.PortBusy)
            {
                hint = "端口 " + Program.Config.Port + " 已被其他程序占用（且不是 DSH）。\n" +
                       "请在 launcher.config.json 中修改 port 与 url，或关闭占用端口的程序。";
            }
            else if (string.IsNullOrEmpty(Program.Config.StartScript))
            {
                hint = "已尝试自动启动 dsh，请确认：\n" +
                       "1. 本机已安装 DeepSeek Harness（dsh 命令可用，或已 `npx @deepseek-ai/dsh web`）\n" +
                       "2. launcher.config.json 的 url/port 配置正确\n\n" +
                       "也可以在该文件中配置 startScript 指定自定义启动脚本。";
            }
            else
            {
                hint = "请检查启动脚本：\n" + Program.Config.StartScript;
            }
            string detail = _svc.LastError;
            MessageBox.Show(this,
                "目标服务未在 " + Program.Config.WaitSeconds + " 秒内就绪。" +
                (string.IsNullOrEmpty(detail) ? "" : "\n\n[" + detail + "]") + "\n\n" + hint,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                if (IsHandleCreated)
                {
                    UnregisterHotKey(Handle, HOTKEY_ID_F11);
                    UnregisterHotKey(Handle, HOTKEY_ID_ESC);
                }
            }
            catch { }
            if (hoverTimer != null) { hoverTimer.Stop(); hoverTimer.Dispose(); hoverTimer = null; }
            if (slideTimer != null) { slideTimer.Dispose(); slideTimer = null; }
            if (Program.Config.AutoHideToolbar) timeEndPeriod(1);
            try
            {
                if (_svc != null) _svc.Dispose();   // 清理：仅杀本应用拉起的子进程
            }
            catch { }
            try
            {
                if (initialized) webView.Dispose();
            }
            catch { }
        }
    }
}

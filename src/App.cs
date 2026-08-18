using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace DshWebLauncher
{
    // 配置默认值，可被 exe 旁 launcher.config.json 覆盖
    internal static class Program
    {
        private const string CoreAsm = "Microsoft.Web.WebView2.Core.dll";
        private const string WinFormsAsm = "Microsoft.Web.WebView2.WinForms.dll";
        private const string LoaderRes = "WebView2Loader.dll";

        internal static string WindowTitle = "Euporiandra's DeepSeek Harness Web Launcher";
        internal static string HomeUrl = "http://127.0.0.1:3080";
        internal static int Port = 3080;
        internal static string StartScript = "";
        internal static int WaitSeconds = 40;
        internal static double WindowScale = 0.72;
        internal static bool AutoHideToolbar = true;
        internal static Size[] Resolutions = new Size[]
        {
            new Size(3840, 2160),
            new Size(2560, 1600)
        };

        // 版本号（界面底部展示）
        internal const string Version = "Ver1.1.4.5";

        [STAThread]
        private static void Main()
        {
            LoadConfig();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // WebView2 托管程序集以嵌入资源方式内置，运行时在此按需加载
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            EnsureLoaderDll();

            Application.Run(new BrowserForm());
        }

        // 读取 exe 同目录 launcher.config.json；缺失或不可读时保持默认值
        private static void LoadConfig()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "launcher.config.json");
                if (!File.Exists(path))
                {
                    // 首次运行：生成默认配置文件，方便用户查看与修改
                    WriteDefaultConfig(path);
                    return;
                }

                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> dict = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                if (dict == null) return;

                object v;
                if (dict.TryGetValue("title", out v)) WindowTitle = Convert.ToString(v);
                if (dict.TryGetValue("url", out v)) HomeUrl = Convert.ToString(v);
                if (dict.TryGetValue("port", out v)) Port = Convert.ToInt32(v);
                if (dict.TryGetValue("startScript", out v)) StartScript = Convert.ToString(v);
                if (dict.TryGetValue("waitSeconds", out v)) WaitSeconds = Convert.ToInt32(v);
                if (dict.TryGetValue("windowScale", out v)) WindowScale = Convert.ToDouble(v);
                if (dict.TryGetValue("autoHideToolbar", out v)) AutoHideToolbar = Convert.ToBoolean(v);
                if (dict.TryGetValue("resolutions", out v))
                {
                    ArrayList arr = v as ArrayList;
                    if (arr != null && arr.Count > 0)
                    {
                        List<Size> list = new List<Size>();
                        foreach (object item in arr)
                        {
                            ArrayList pair = item as ArrayList;
                            if (pair != null && pair.Count == 2)
                                list.Add(new Size(Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1])));
                        }
                        if (list.Count > 0) Resolutions = list.ToArray();
                    }
                }
            }
            catch { }
        }

        // 首次运行：在 exe 同目录生成默认配置（与内置默认值一致），用户可自行编辑
        private static void WriteDefaultConfig(string path)
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"title\": " + JsonStr(WindowTitle) + ",");
                sb.AppendLine("  \"url\": " + JsonStr(HomeUrl) + ",");
                sb.AppendLine("  \"port\": " + Port + ",");
                sb.AppendLine("  \"startScript\": " + JsonStr(StartScript) + ",");
                sb.AppendLine("  \"waitSeconds\": " + WaitSeconds + ",");
                sb.AppendLine("  \"windowScale\": " + WindowScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ",");
                sb.AppendLine("  \"autoHideToolbar\": " + (AutoHideToolbar ? "true" : "false") + ",");
                sb.Append("  \"resolutions\": [");
                for (int i = 0; i < Resolutions.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append("[" + Resolutions[i].Width + ", " + Resolutions[i].Height + "]");
                }
                sb.AppendLine("]");
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // JSON 字符串转义（引号/反斜杠/控制字符）
        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            System.Text.StringBuilder sb = new System.Text.StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u" + ((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append("\"");
            return sb.ToString();
        }

        // 从嵌入资源加载 WebView2 托管 DLL
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string resName = null;
            if (name == "Microsoft.Web.WebView2.Core") resName = CoreAsm;
            else if (name == "Microsoft.Web.WebView2.WinForms") resName = WinFormsAsm;
            if (resName == null) return null;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
            {
                if (s == null) return null;
                byte[] data = new byte[s.Length];
                s.Read(data, 0, data.Length);
                return Assembly.Load(data);
            }
        }

        // 首次运行把内嵌的原生 WebView2Loader.dll 释放到 exe 目录
        private static void EnsureLoaderDll()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string target = Path.Combine(dir, LoaderRes);
                if (!File.Exists(target))
                {
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(LoaderRes))
                    {
                        if (s == null) return;
                        using (FileStream fs = new FileStream(target, FileMode.Create, FileAccess.Write))
                        {
                            s.CopyTo(fs);
                        }
                    }
                }
            }
            catch { }
        }

        // 800ms 超时的端口存活检测
        internal static bool IsPortOpen()
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", Port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(800);
                    if (ok) client.EndConnect(ar);
                    return ok && client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        // 服务未运行时拉起 DSH：优先用配置的启动脚本；
        // 未配置脚本时自动发现 dsh CLI（PATH / 常见安装位置）并隐藏启动 dsh web
        internal static void StartDshIfNeeded()
        {
            try
            {
                if (!string.IsNullOrEmpty(StartScript) && File.Exists(StartScript))
                {
                    LaunchHidden(StartScript, "", Path.GetDirectoryName(StartScript));
                    return;
                }

                // 未配置脚本：尝试自动启动 dsh CLI
                string cli = FindDshCli();
                if (cli != null)
                {
                    LaunchHidden(cli, "web", null);
                }
            }
            catch { }
        }

        // 通过 cmd /c 隐藏启动批处理/命令（UseShellExecute=false 的 CreateProcess 路径，
        // 在沙箱与常规环境下都更可靠；.cmd/.bat 必须经 cmd.exe 包装）
        private static void LaunchHidden(string file, string args, string workDir)
        {
            Process p = new Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = "/c \"\"" + file + "\"" + (args.Length > 0 ? " " + args : "") + "\"";
            if (!string.IsNullOrEmpty(workDir)) p.StartInfo.WorkingDirectory = workDir;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p.Start();
        }

        // 在 PATH 与常见安装位置查找 dsh 命令
        private static string FindDshCli()
        {
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] names = { "dsh.cmd", "dsh.bat", "dsh.exe", "dsh" };
                foreach (string dirRaw in pathEnv.Split(';'))
                {
                    string dir = dirRaw.Trim();
                    if (dir.Length == 0) continue;
                    foreach (string n in names)
                    {
                        string full = Path.Combine(dir, n);
                        if (File.Exists(full)) return full;
                    }
                }
                // 常见 npm 全局安装位置
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string[] candidates = {
                    Path.Combine(appData, "npm", "dsh.cmd"),
                    Path.Combine(appData, "npm", "dsh"),
                    @"C:\Program Files\nodejs\dsh.cmd",
                    @"C:\Program Files\nodejs\dsh",
                    @"C:\Program Files\nodejs\dsh.exe"
                };
                foreach (string c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }
    }

    internal sealed class BrowserForm : Form
    {
        // 全局热键：F11 全屏 / Esc 退出（解决焦点在 WebView2 内容时按键不经过本进程消息泵的问题）
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_F11 = 0x7A;
        private const uint VK_ESCAPE = 0x1B;
        private const int HOTKEY_ID_F11 = 1;
        private const int HOTKEY_ID_ESC = 2;
        private bool escHotkeyRegistered = false;

        private Microsoft.Web.WebView2.WinForms.WebView2 webView;
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
        private System.Threading.Timer slideTimer;   // 高精度动画定时器（线程池，8ms 触发）
        private System.Diagnostics.Stopwatch slideClock = System.Diagnostics.Stopwatch.StartNew();
        private double slideStartY, slideEndY, slideStartMs;   // 动画起止位置与开始时间
        private int toolbarTargetY = int.MinValue;       // 动画目标 Y（0=显示，负值=隐藏）
        private const int HoverZoneHeight = 4;   // 顶部触发区高度（像素）
        private const double SlideDurationMs = 260;  // 动画时长 ms（ease-out 全程）
        private object slideLock = new object(); // 动画状态锁

        public BrowserForm()
        {
            Text = Program.WindowTitle;
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

            // 初始窗口 = 屏幕 × windowScale，不满屏
            Size screen = Screen.PrimaryScreen.Bounds.Size;
            double scale = Program.WindowScale;
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

            // 顶部导航条自动隐藏：常态隐藏，鼠标移到窗口顶部时平滑滑入，移开时滑出
            if (Program.AutoHideToolbar)
            {
                toolStrip.Dock = DockStyle.None;   // 手动定位以便滑动动画
                toolStrip.Visible = false;
                timeBeginPeriod(1);                // 提高定时器分辨率到 1ms（动画触发精度）
                slideTimer = new System.Threading.Timer(OnSlideTick, null,
                    System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);   // 手动启动
                hoverTimer = new System.Windows.Forms.Timer();
                hoverTimer.Interval = 150;
                hoverTimer.Tick += OnHoverTick;
                hoverTimer.Start();
            }

            Shown += OnFirstShown;
            Resize += OnWindowResize;
            Load += OnFormLoad;
            FormClosing += OnFormClosing;

            // 全局热键注册需窗口句柄已创建：在 HandleCreated 后注册 F11，
            // Esc 仅在全屏状态下临时注册（避免干扰页面内的 Esc 输入）
            HandleCreated += (s2, e2) => RegisterHotKey(Handle, HOTKEY_ID_F11, MOD_NOREPEAT, VK_F11);
        }

        // 处理全局热键消息（WM_HOTKEY）
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                // 仅当本窗口（或其子窗口）为前台时才响应，避免抢占其他程序的热键
                if (IsForegroundApp())
                {
                    if (id == HOTKEY_ID_F11)
                    {
                        ToggleFullscreen(!isFullscreen);
                        return;
                    }
                    if (id == HOTKEY_ID_ESC && isFullscreen)
                    {
                        ToggleFullscreen(false);
                        return;
                    }
                }
            }
            base.WndProc(ref m);
        }

        // 前台窗口是否为本窗体或其子窗口（含 WebView2 渲染窗口）
        private bool IsForegroundApp()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == Handle) return true;
            return IsChild(Handle, fg);
        }

        // 全屏时注册 Esc 热键，退出时注销
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
        // 提高系统定时器分辨率到 1ms，让 120fps 动画真正生效（默认 ~15.6ms 会被限制到 ~64Hz）
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        // 首次显示后初始化工具栏动画状态（此时工具栏高度已确定）
        private void OnFirstShown(object sender, EventArgs e)
        {
            toolbarTargetY = -toolStrip.Height;      // 初始：完全隐藏在窗口上方
            toolStrip.Location = new Point(0, toolbarTargetY);
            toolStrip.Visible = false;
        }

        // 轮询鼠标位置：位于窗口顶部触发区或导航条上时滑入，否则滑出
        private void OnHoverTick(object sender, EventArgs e)
        {
            if (!IsHandleCreated || !Visible || !Program.AutoHideToolbar) return;
            if (toolbarTargetY == int.MinValue) return;   // 尚未初始化
            Point cursor = PointToClient(Cursor.Position);
            bool overToolbar = toolStrip.Bounds.Contains(cursor);
            bool nearTop = cursor.Y >= 0 && cursor.Y <= HoverZoneHeight;
            bool show = overToolbar || nearTop;
            int target = show ? 0 : -toolStrip.Height;
            if (target != toolbarTargetY)
            {
                toolbarTargetY = target;
                if (show)
                {
                    toolStrip.Visible = true;      // 滑入前先可见
                    toolStrip.BringToFront();
                }
                StartSlide(target);
            }
        }

        // 启动滑动动画：记录起始状态，定时器以 8ms 触发（目标 120fps）
        private void StartSlide(int targetY)
        {
            slideStartY = toolStrip.Top;
            slideEndY = targetY;
            slideStartMs = slideClock.Elapsed.TotalMilliseconds;
            slideTimer.Change(0, 8);
        }

        // 滑动动画：线程池定时器触发，回到 UI 线程按真实时间插值渲染
        private void OnSlideTick(object state)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(AnimateSlideFrame));
            }
            catch { }
        }

        // 按真实时间推进动画帧（ease-out cubic，动画时长恒定，不受触发抖动影响）
        private void AnimateSlideFrame()
        {
            if (IsDisposed) return;
            double t = (slideClock.Elapsed.TotalMilliseconds - slideStartMs) / SlideDurationMs;
            if (t >= 1.0)
            {
                toolStrip.Location = new Point(0, (int)slideEndY);
                slideTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                if (slideEndY < 0) toolStrip.Visible = false;   // 完全滑出后隐藏
                return;
            }
            double ease = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
            int y = (int)(slideStartY + (slideEndY - slideStartY) * ease);
            toolStrip.Location = new Point(0, y);
        }

        // 窗口尺寸变化时保持工具栏宽度与位置
        private void SyncToolbar()
        {
            if (toolStrip == null) return;
            toolStrip.Width = ClientSize.Width;
            if (toolbarTargetY == int.MinValue) return;
            toolStrip.Location = new Point(0, toolbarTargetY);
        }

        // 全屏：记状态→隐藏栏/边框→铺满所在显示器；退出时原样恢复
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
                // 全屏时工具栏滑出隐藏（若动画已初始化）
                if (toolbarTargetY != int.MinValue)
                {
                    toolbarTargetY = -toolStrip.Height;
                    toolStrip.Visible = false;
                    slideTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    toolStrip.Location = new Point(0, toolbarTargetY);
                }
                statusLabel.Owner.Visible = false;
                Bounds = Screen.FromHandle(Handle).Bounds;
                isFullscreen = true;
                UpdateEscHotkey();   // 注册 Esc 热键
                LayoutWebView();
            }
            else
            {
                FormBorderStyle = savedBorder;
                statusLabel.Owner.Visible = savedStatusVisible;
                Bounds = savedBounds;
                isFullscreen = false;
                // 退出全屏后由 hover 轮询按鼠标位置决定工具栏显示
                UpdateEscHotkey();   // 注销 Esc 热键
                LayoutWebView();
            }
        }

        // 下拉第一项为"跟随窗口"，其余为配置的固定分辨率档位
        private bool IsFixedResolution()
        {
            return cmbResolution != null && cmbResolution.SelectedIndex > 0;
        }

        private Size CurrentRenderSize()
        {
            if (cmbResolution != null && cmbResolution.SelectedIndex > 0)
                return Program.Resolutions[cmbResolution.SelectedIndex - 1];
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
            btnHome.Click += (s, e) => { if (initialized) webView.CoreWebView2.Navigate(Program.HomeUrl); };

            cmbResolution = new ToolStripComboBox();
            cmbResolution.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbResolution.Items.Add("跟随窗口（推荐）");
            foreach (Size r in Program.Resolutions)
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
            webView = new Microsoft.Web.WebView2.WinForms.WebView2();
            webView.BackColor = Color.Black;
            hostPanel.Controls.Add(webView);
        }

        private void BuildStatusStrip()
        {
            StatusStrip strip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("少女祈祷中...");
            strip.Items.Add(statusLabel);
            // 界面最下方右侧：出品信息与版本号
            ToolStripStatusLabel brand = new ToolStripStatusLabel("艾珀莉亚出品 · " + Program.Version);
            brand.Alignment = ToolStripItemAlignment.Right;
            strip.Items.Add(brand);
            Controls.Add(strip);
        }

        // 布局：跟随窗口模式填满客户区；固定分辨率模式等比缩放居中
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

            // 固定分辨率核心：控件物理尺寸与 RasterizationScale 成对更新，
            // 使 逻辑视口 = 物理尺寸 / RasterizationScale 与目标分辨率对等。
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

        // 渲染模式切换：固定分辨率用 raw pixels；跟随窗口恢复系统 DPI
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
                renderSize = Program.Resolutions[cmbResolution.SelectedIndex - 1];
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

        // WinForms WebView2 未公开 Controller，只能反射取内部字段
        private CoreWebView2Controller GetController()
        {
            if (webView == null) return null;
            System.Reflection.FieldInfo f = typeof(Microsoft.Web.WebView2.WinForms.WebView2)
                .GetField("_coreWebView2Controller",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return f == null ? null : f.GetValue(webView) as CoreWebView2Controller;
        }

        private async void OnFormLoad(object sender, EventArgs e)
        {
            toolStrip.BringToFront();
            statusLabel.Text = "少女祈祷中...";
            if (!Program.IsPortOpen())
            {
                statusLabel.Text = string.IsNullOrEmpty(Program.StartScript)
                    ? "服务未运行，尝试自动启动 DSH..."
                    : "服务未运行，正在调用启动脚本...";
                Program.StartDshIfNeeded();
                for (int i = 0; i < Program.WaitSeconds; i++)
                {
                    await Task.Delay(1000);
                    if (Program.IsPortOpen()) break;
                    if (i % 5 == 4)
                        statusLabel.Text = string.Format("少女祈祷中 ({0}/{1} 秒)...", i + 1, Program.WaitSeconds);
                }
            }

            if (!Program.IsPortOpen())
            {
                statusLabel.Text = "启动失败：服务未就绪。";
                MessageBox.Show(this,
                    "目标服务未在 " + Program.WaitSeconds + " 秒内就绪。\n" +
                    (string.IsNullOrEmpty(Program.StartScript)
                        ? "已尝试自动启动 dsh，请确认：\n1. 本机已安装 DeepSeek Harness（dsh 命令可用）\n2. launcher.config.json 的 url/port 配置正确\n\n也可以在该文件中配置 startScript 指定自定义启动脚本。"
                        : "请检查启动脚本：\n" + Program.StartScript),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                await webView.EnsureCoreWebView2Async(null);
                initialized = true;
                ApplyRenderMode();
                LayoutWebView();

                webView.CoreWebView2.Navigate(Program.HomeUrl);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

                webView.NavigationStarting += (s2, e2) =>
                {
                    statusLabel.Text = "少女祈祷中...";
                };
                webView.NavigationCompleted += async (s2, e2) =>
                {
                    string uri = webView.CoreWebView2.Source;
                    txtUrl.Text = uri;
                    if (e2.IsSuccess)
                    {
                        string js = "window.innerWidth + ' x ' + window.innerHeight + ' @ ' + window.devicePixelRatio";
                        string vp = "";
                        try
                        {
                            vp = await webView.CoreWebView2.ExecuteScriptAsync(js);
                            vp = vp.Trim('"');
                        }
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
            if (Program.AutoHideToolbar) timeEndPeriod(1);   // 还原定时器分辨率
            try
            {
                if (initialized) webView.Dispose();
            }
            catch { }
        }
    }
}

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
        internal static Size[] Resolutions = new Size[]
        {
            new Size(3840, 2160),
            new Size(2560, 1600)
        };

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
                if (!File.Exists(path)) return;

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

        // 服务未运行且配置了 startScript 时，隐藏调用启动脚本
        internal static void StartDshIfNeeded()
        {
            if (string.IsNullOrEmpty(StartScript)) return;
            try
            {
                if (File.Exists(StartScript))
                {
                    Process p = new Process();
                    p.StartInfo.FileName = StartScript;
                    p.StartInfo.WorkingDirectory = Path.GetDirectoryName(StartScript);
                    p.StartInfo.UseShellExecute = true;
                    p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    p.Start();
                }
            }
            catch { }
        }
    }

    internal sealed class BrowserForm : Form
    {
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

            Resize += OnWindowResize;
            Load += OnFormLoad;
            FormClosing += OnFormClosing;
            KeyPreview = true;
            KeyDown += OnGlobalKeyDown;
        }

        // F11 全屏 / Esc 退出全屏
        private void OnGlobalKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
            {
                e.Handled = true;
                ToggleFullscreen(true);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (isFullscreen)
                {
                    e.Handled = true;
                    ToggleFullscreen(false);
                }
            }
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
                toolStrip.Visible = false;
                statusLabel.Owner.Visible = false;
                Bounds = Screen.FromHandle(Handle).Bounds;
                isFullscreen = true;
                LayoutWebView();
            }
            else
            {
                FormBorderStyle = savedBorder;
                toolStrip.Visible = savedToolStripVisible;
                statusLabel.Owner.Visible = savedStatusVisible;
                Bounds = savedBounds;
                isFullscreen = false;
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
                if (string.IsNullOrEmpty(Program.StartScript))
                {
                    statusLabel.Text = "少女祈祷中...";
                }
                else
                {
                    statusLabel.Text = "少女祈祷中...";
                }
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
                        ? "请确认目标服务已运行，或检查 launcher.config.json 的 url/port 配置。"
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
                if (initialized) webView.Dispose();
            }
            catch { }
        }
    }
}

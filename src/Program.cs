using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace DshWebLauncher
{
    /// <summary>
    /// 程序入口（ver1.1.5.0 重构）：
    ///  - 配置加载（LauncherConfig）
    ///  - 单实例 Mutex + 复用已有窗口
    ///  - CLI 自测模式（--selftest / --monitor-test），便于无 GUI 环境验证自动启动链路
    ///  - WebView2 托管 DLL 以嵌入资源方式内置，运行时按需加载
    /// </summary>
    internal static class Program
    {
        private const string CoreAsm = "Microsoft.Web.WebView2.Core.dll";
        private const string WinFormsAsm = "Microsoft.Web.WebView2.WinForms.dll";
        private const string LoaderRes = "WebView2Loader.dll";

        // 版本号（界面底部展示）
        internal const string Version = "Ver1.1.5.0";

        internal static LauncherConfig Config;

        [STAThread]
        private static int Main(string[] args)
        {
            Config = LauncherConfig.Load();
            Log.Init(Config.EnableLog);
            Log.Info("==== DSH Web Launcher " + Version + " 启动 ====");
            Log.Info("url=" + Config.HomeUrl + " port=" + Config.Port +
                     " startScript=" + (string.IsNullOrEmpty(Config.StartScript) ? "(空)" : Config.StartScript) +
                     " autoRestart=" + Config.AutoRestart + " maxRetries=" + Config.MaxStartRetries);

            // CLI 自测模式（无 GUI）
            string cli = null;
            int cliSeconds = 0;
            int cliPort = 0;
            int holdSeconds = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--selftest") cli = "selftest";
                else if (args[i] == "--monitor-test" && i + 1 < args.Length) { cli = "monitor"; int.TryParse(args[++i], out cliSeconds); }
                else if (args[i] == "--port" && i + 1 < args.Length) { int.TryParse(args[++i], out cliPort); }
                else if (args[i] == "--hold" && i + 1 < args.Length) { int.TryParse(args[++i], out holdSeconds); }
                else if (args[i] == "--version") cli = "version";
            }
            if (cli != null)
            {
                if (cliPort > 0 && cliPort <= 65535)
                {
                    Config.Port = cliPort;
                    Config.HomeUrl = "http://127.0.0.1:" + cliPort;
                }
                return RunCli(cli, cliSeconds, holdSeconds);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例：已有实例时复用其窗口
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\DSHWebLauncher_SingleInstance_9f3c2e17", out createdNew))
            {
                if (!createdNew)
                {
                    SignalFirstInstance();
                    return 0;
                }

                // WebView2 托管程序集以嵌入资源方式内置
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                EnsureLoaderDll();

                Application.Run(new BrowserForm());
            }
            return 0;
        }

        // =============== CLI 自测 ===============

        private static int RunCli(string mode, int seconds, int holdSeconds)
        {
            ServiceStarter svc = new ServiceStarter(Config);
            switch (mode)
            {
                case "version":
                    Console.WriteLine(Version);
                    return 0;

                case "selftest":
                {
                    ServiceStatus s = svc.Probe();
                    Console.WriteLine("=== DSH Web Launcher 自检 ===");
                    Console.WriteLine("版本:        " + Version);
                    Console.WriteLine("目标URL:     " + Config.HomeUrl);
                    Console.WriteLine("startScript: " + (string.IsNullOrEmpty(Config.StartScript) ? "(空)" : Config.StartScript));
                    Console.WriteLine("状态:        " + s);
                    Console.WriteLine("详情:        " + svc.Detail);
                    Console.WriteLine("dsh CLI:     " + (DshLocator.FindDshCli() ?? "未找到"));
                    Console.WriteLine("node.exe:    " + (DshLocator.FindNode() ?? "未找到"));
                    Console.WriteLine("bin.js:      " + (DshLocator.FindDshBinJsDirect() ?? "未找到"));
                    return s == ServiceStatus.Online ? 0 : 1;
                }

                case "monitor":
                {
                    if (seconds <= 0) seconds = Config.WaitSeconds + 5;
                    Console.WriteLine("=== 监控测试（自动启动链路）===");
                    Console.WriteLine("将在 " + seconds + " 秒内验证：探测 → 三级兜底拉起 → 等待就绪" +
                        (holdSeconds > 0 ? " → 保持 " + holdSeconds + " 秒观察 AutoRestart 监控" : ""));
                    svc.StateChanged += delegate
                    {
                        Console.WriteLine("  [" + DateTime.Now.ToString("HH:mm:ss") + "] " + svc.Status + " | " + svc.Detail);
                    };
                    bool ok = svc.StartIfNeeded();
                    Console.WriteLine("--- 最终结果 ---");
                    Console.WriteLine("就绪:        " + ok);
                    Console.WriteLine("状态:        " + svc.Status);
                    Console.WriteLine("OwnsProcess: " + svc.OwnsProcess + "  ChildPid: " + svc.ChildPid);
                    if (!ok && svc.LastError != null) Console.WriteLine("错误:        " + svc.LastError);

                    // 保持观察：给 AutoRestart watchdog 时间触发"失联→重启"（仅本应用拉起的服务）
                    if (ok && svc.OwnsProcess && holdSeconds > 0)
                    {
                        Console.WriteLine("保持观察 " + holdSeconds + " 秒，观察服务失联后的自动重启…");
                        System.Threading.Thread.Sleep(holdSeconds * 1000);
                        Console.WriteLine("观察结束。最终状态: " + svc.Status + " | " + svc.Detail);
                    }

                    // 清理：如果是本应用拉起的，杀掉，避免测试后残留服务
                    if (svc.OwnsProcess)
                    {
                        Console.WriteLine("清理自启服务…");
                        svc.KillChild();
                    }
                    svc.Dispose();
                    return ok ? 0 : 1;
                }
            }
            return 1;
        }

        // =============== 单实例 ===============

        private static void SignalFirstInstance()
        {
            try
            {
                int self = System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("DSHWebLauncher"))
                {
                    if (p.Id == self) continue;
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                        SetForegroundWindow(p.MainWindowHandle);
                    }
                    break;
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // =============== 嵌入资源 ===============

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
    }
}

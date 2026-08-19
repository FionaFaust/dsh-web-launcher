using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DshWebLauncher
{
    /// <summary>服务状态（供 UI 展示）。</summary>
    internal enum ServiceStatus
    {
        Unknown,
        Checking,
        Online,          // 端口在线且验证为 DSH
        PortBusy,        // 端口被非 DSH 进程占用
        Starting,        // 已拉起，等待就绪
        Restarting,      // 服务挂掉，自动重启中
        Failed,          // 拉起失败 / 超时（含原因）
        Stopped          // 服务未运行且未启动
    }

    /// <summary>
    /// DSH 服务自动启动核心（ver1.1.5.0 重构）。
    ///
    /// 职责：
    ///   1. 探测服务是否在线（TCP + HTTP 双重验证，杜绝"端口被占但非 DSH"误判）；
    ///   2. 未在线时按 startScript → dsh CLI → node+bin.js 直启 三级兜底拉起；
    ///   3. 启动后等待就绪（WaitSeconds 循环），失败按 MaxStartRetries 退避重试；
    ///   4. 服务就绪后可选监控（AutoRestart），挂掉自动重新拉起；
    ///   5. 全程落盘日志（Log）。
    ///
    /// 与 UI 解耦：只通过回调/事件上报状态，可独立于窗体测试。
    /// </summary>
    internal sealed class ServiceStarter
    {
        private readonly LauncherConfig _cfg;
        private readonly string _exeDir;

        public ServiceStatus Status { get; private set; }
        public string Detail { get; private set; }
        public bool OwnsProcess { get; private set; }   // 当前服务是否由本应用拉起
        public int ChildPid { get; private set; }        // 拉起的子进程 pid（-1 = 无）
        public string LastError { get; private set; }
        public string NodePath { get; private set; }
        public string DshBinJs { get; private set; }
        public string CliPath { get; private set; }

        public event Action StateChanged;

        private Process _child;                 // 拉起的 cmd/node 进程（或 null）
        private Timer _watchTimer;              // 监控定时器
        private bool _disposed;

        public ServiceStarter(LauncherConfig cfg)
        {
            _cfg = cfg;
            _exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            Status = ServiceStatus.Unknown;
            Detail = "未初始化";
        }

        // =============== 探测 ===============

        /// <summary>TCP 探测：端口是否在监听（800ms 超时，不阻塞调用线程）。</summary>
        public bool IsPortOpen()
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", _cfg.Port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(800);
                    if (ok) client.EndConnect(ar);
                    return ok && client.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// HTTP 验证：GET {url}/ 返回 2xx 且响应体包含 DSH 注入标记 __DSH_BOOT__，
        /// 才认为端口上是 DSH。避免端口被别的 Web 服务占用时误加载。
        /// </summary>
        public bool IsDshServing()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_cfg.HomeUrl + "/");
                req.Timeout = 2500;
                req.ReadWriteTimeout = 2500;
                req.AllowAutoRedirect = true;
                req.UserAgent = "DSHWebLauncher/" + Program.Version;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode < 200 || (int)resp.StatusCode >= 300) return false;
                    using (Stream s = resp.GetResponseStream())
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8, true, 4096))
                    {
                        char[] buf = new char[4096];
                        int read = r.Read(buf, 0, buf.Length);
                        string head = new string(buf, 0, read);
                        return head.IndexOf("__DSH_BOOT__", StringComparison.Ordinal) >= 0;
                    }
                }
            }
            catch { return false; }
        }

        /// <summary>完整探测：先 TCP，再 HTTP 验证 DSH 身份。返回详细状态。</summary>
        public ServiceStatus Probe()
        {
            if (!IsPortOpen())
            {
                SetStatus(ServiceStatus.Stopped, "服务未运行（端口 " + _cfg.Port + " 空闲）");
                return Status;
            }
            if (IsDshServing())
            {
                SetStatus(ServiceStatus.Online, "服务在线（已连接现有实例）");
                OwnsProcess = false;
                return Status;
            }
            SetStatus(ServiceStatus.PortBusy, "端口 " + _cfg.Port + " 被非 DSH 进程占用");
            return Status;
        }

        // =============== 启动 ===============

        /// <summary>
        /// 尝试自动启动：探测 → 若未在线则按三级兜底拉起 → 等待就绪。
        /// 返回最终是否就绪（Online）。
        /// </summary>
        public bool StartIfNeeded()
        {
            Log.Info("StartIfNeeded: 开始探测 " + _cfg.HomeUrl);
            ServiceStatus s = Probe();
            if (s == ServiceStatus.Online) return true;
            if (s == ServiceStatus.PortBusy)
            {
                Log.Warn("端口被非 DSH 进程占用，跳过自动启动");
                return false;
            }

            // 三级兜底拉起
            if (!TryLaunch())
            {
                SetStatus(ServiceStatus.Failed, LastError);
                return false;
            }

            // 等待就绪：WaitSeconds 内每秒探测；每次探测同时做 TCP + HTTP 验证
            for (int i = 0; i < _cfg.WaitSeconds; i++)
            {
                Thread.Sleep(1000);
                if (IsDshServing())
                {
                    SetStatus(ServiceStatus.Online, "服务就绪（由本应用启动）");
                    OwnsProcess = true;
                    StartWatchdogIfNeeded();
                    return true;
                }
                if ((i + 1) % 5 == 0)
                    SetStatus(ServiceStatus.Starting, "等待服务就绪 " + (i + 1) + "/" + _cfg.WaitSeconds + " 秒…");
            }

            // 超时：按 MaxStartRetries 退避重试
            int retries = _cfg.MaxStartRetries;
            int attempt = 1;
            while (attempt <= retries)
            {
                Log.Warn("启动超时，第 " + attempt + " 次重试（退避 " + BackoffMs(attempt) + "ms）");
                SetStatus(ServiceStatus.Restarting, "启动超时，重试 " + attempt + "/" + retries + "…");
                Thread.Sleep(BackoffMs(attempt));
                KillChild();
                if (!TryLaunch())
                {
                    SetStatus(ServiceStatus.Failed, LastError);
                    return false;
                }
                for (int i = 0; i < _cfg.WaitSeconds; i++)
                {
                    Thread.Sleep(1000);
                    if (IsDshServing())
                    {
                        SetStatus(ServiceStatus.Online, "服务就绪（重试成功，由本应用启动）");
                        OwnsProcess = true;
                        StartWatchdogIfNeeded();
                        return true;
                    }
                }
                attempt++;
            }

            LastError = "启动超时：dsh 服务在 " + _cfg.WaitSeconds + " 秒内未就绪（已重试 " + retries + " 次）";
            Log.Error(LastError);
            SetStatus(ServiceStatus.Failed, LastError);
            return false;
        }

        /// <summary>退避毫秒：2s → 4s → 8s（封顶 8s）。</summary>
        private static int BackoffMs(int attempt)
        {
            int ms = 2000;
            for (int i = 1; i < attempt; i++) ms *= 2;
            return ms > 8000 ? 8000 : ms;
        }

        /// <summary>
        /// 三级兜底拉起（ver1.1.5.0+ 修复：失败降级，不再"启动即成功"）：
        ///   1. startScript 配置且存在 → cmd /c 运行脚本；
        ///   2. 否则 FindDshCli()（PATH/常见位置）；
        ///   3. 再否则 node.exe + @deepseek-ai/dsh/lib/bin.js 直启（覆盖 npx/全局 npm 安装）。
        /// 每一级启动后做秒退检测：进程在 ~1.5s 内退出且退出码非 0（说明 CLI 本身起不来，
        /// 例如 dsh.cmd 依赖的 node 不在 PATH），则降级尝试下一级，而不是直接返回"成功"。
        /// 返回是否成功拉起进程。失败时填充 LastError。
        /// </summary>
        private bool TryLaunch()
        {
            // 第一级：配置的启动脚本
            if (!string.IsNullOrEmpty(_cfg.StartScript))
            {
                string script = _cfg.StartScript;
                if (File.Exists(script))
                {
                    string workDir = ResolveWorkspaceDir(Path.GetDirectoryName(script));
                    if (LaunchHidden(script, "", workDir))
                    {
                        CliPath = script;
                        Log.Info("已启动脚本: " + script + " (cwd=" + workDir + ")");
                        return true;
                    }
                    Log.Warn("startScript 启动失败，降级到 dsh CLI: " + LastError);
                }
                else
                {
                    LastError = "配置的 startScript 不存在: " + script;
                    Log.Error(LastError);
                }
            }

            // 第二级：PATH 上的 dsh CLI
            string cli = DshLocator.FindDshCli();
            if (!string.IsNullOrEmpty(cli) && File.Exists(cli))
            {
                if (LaunchHidden(cli, "web", ResolveWorkspaceDir(null)))
                {
                    CliPath = cli;
                    Log.Info("已启动 dsh CLI: " + cli + " web");
                    return true;
                }
                Log.Warn("dsh CLI 启动失败，降级到 node+bin.js 直启: " + LastError);
            }

            // 第三级兜底：node + bin.js 直启（覆盖 npx 缓存 / 全局 npm 安装但 PATH 未暴露的情况）
            string node = DshLocator.FindNode();
            string binJs = FindBinJsWithShimFallback(cli);
            if (!string.IsNullOrEmpty(node) && !string.IsNullOrEmpty(binJs) && File.Exists(node) && File.Exists(binJs))
            {
                if (LaunchHidden(node, "\"" + binJs + "\" web --host 127.0.0.1 --port " + _cfg.Port, ResolveWorkspaceDir(null)))
                {
                    NodePath = node;
                    DshBinJs = binJs;
                    CliPath = node;
                    Log.Info("已直启 node+bin.js: " + node + " " + binJs);
                    return true;
                }
                Log.Warn("node+bin.js 直启失败: " + LastError);
            }

            LastError = BuildNotFoundMessage(cli);
            Log.Error(LastError);
            return false;
        }

        /// <summary>合并 shim 解析与直接查找，返回 bin.js 路径。</summary>
        private static string FindBinJsWithShimFallback(string cli)
        {
            if (!string.IsNullOrEmpty(cli) && cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                string viaShim = DshLocator.ResolveBinJsFromShim(cli);
                if (viaShim != null) return viaShim;
            }
            return DshLocator.FindDshBinJsDirect();
        }

        /// <summary>未找到 dsh 时给出可操作的错误信息。</summary>
        private static string BuildNotFoundMessage(string cliFound)
        {
            if (cliFound == null)
                return "未找到 dsh CLI 与 @deepseek-ai/dsh 安装。请先执行 `npx @deepseek-ai/dsh web` 安装，" +
                       "或在 launcher.config.json 配置 startScript 指向启动脚本。";
            return "已找到 dsh CLI 但启动失败，且无法定位 bin.js 直启：" + cliFound;
        }

        /// <summary>解析工作目录：显式配置 → startScript 目录 → exe 目录。</summary>
        private string ResolveWorkspaceDir(string fallback)
        {
            if (!string.IsNullOrEmpty(_cfg.WorkspaceRoot))
            {
                try { if (Directory.Exists(_cfg.WorkspaceRoot)) return _cfg.WorkspaceRoot; }
                catch { }
            }
            if (!string.IsNullOrEmpty(fallback) && Directory.Exists(fallback)) return fallback;
            return _exeDir;
        }

        /// <summary>
        /// 通过 cmd /c 隐藏启动（UseShellExecute=false 的 CreateProcess 路径，
        /// 沙箱与常规环境下都更可靠；.cmd/.bat 必须经 cmd.exe 包装）。
        /// 记录子进程 pid，供后续 KillChild 清理。失败返回 false 并填充 LastError。
        /// </summary>
        private bool LaunchHidden(string file, string args, string workDir)
        {
            try
            {
                Process p = new Process();
                p.StartInfo.FileName = "cmd.exe";
                p.StartInfo.Arguments = "/c \"\"" + file + "\"" + (args.Length > 0 ? " " + args : "") + "\"";
                if (!string.IsNullOrEmpty(workDir)) p.StartInfo.WorkingDirectory = workDir;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                if (!p.Start())
                {
                    LastError = "Process.Start 失败: " + file;
                    return false;
                }
                _child = p;
                try { ChildPid = p.Id; } catch { ChildPid = -1; }
                Log.Info("子进程已启动 pid=" + ChildPid + " cmd: " + file + " " + args);

                // 秒退检测：cmd /c 解析脚本需要一点点时间。
                // 若进程在 ~1.5s 内退出且退出码非 0，说明被启动的 CLI/脚本本身起不来
                // （例如 dsh.cmd 内部依赖的 node 不在 PATH），视为启动失败供上层降级。
                // 退出码 0 的快速退出是"fire-and-forget"脚本的合法形态（如 start 后立即结束），不判失败。
                try
                {
                    if (p.WaitForExit(1500) && p.ExitCode != 0)
                    {
                        LastError = "启动失败: " + file + " 秒退退出码=" + p.ExitCode;
                        Log.Warn(LastError);
                        _child = null;
                        try { ChildPid = -1; } catch { }
                        try { p.Dispose(); } catch { }
                        return false;
                    }
                }
                catch { /* 进程存活检查失败不阻断，继续视为已启动 */ }

                SetStatus(ServiceStatus.Starting, "已启动 dsh (pid=" + ChildPid + ")，等待就绪…");
                return true;
            }
            catch (Exception ex)
            {
                LastError = "启动失败: " + file + " → " + ex.Message;
                Log.Error(LastError);
                return false;
            }
        }

        /// <summary>杀掉由本应用拉起的进程树（安全：只有 OwnsProcess 的子进程）。
        /// 用 taskkill /T 递归杀 cmd 及其派生的 node 子进程，避免"父进程死、服务残留"。</summary>
        public void KillChild()
        {
            Process p = _child;
            _child = null;
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        Log.Info("清理进程树 pid=" + p.Id);
                        // /T 递归杀子进程树；/F 强制。失败时回退到直接 Kill。
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo();
                            psi.FileName = "taskkill.exe";
                            psi.Arguments = "/PID " + p.Id + " /T /F";
                            psi.CreateNoWindow = true;
                            psi.UseShellExecute = false;
                            using (Process tk = Process.Start(psi))
                            {
                                if (tk != null) tk.WaitForExit(5000);
                            }
                        }
                        catch
                        {
                            p.Kill();
                        }
                        p.WaitForExit(3000);
                    }
                }
                catch { }
                try { p.Dispose(); } catch { }
            }
            ChildPid = -1;
            OwnsProcess = false;
        }

        // =============== 监控（兜底：服务挂掉自动重启） ===============

        private void StartWatchdogIfNeeded()
        {
            if (!_cfg.AutoRestart || _watchTimer != null || _disposed) return;
            _watchTimer = new Timer(OnWatchTick, null, 5000, 3000);
            Log.Info("健康监控已启动（3s 周期，AutoRestart=" + _cfg.AutoRestart + "）");
        }

        private void OnWatchTick(object state)
        {
            try
            {
                if (_disposed) return;
                if (IsDshServing()) return;   // 一切正常
                Log.Warn("监控探测：服务失联，触发自动重启");
                SetStatus(ServiceStatus.Restarting, "服务失联，自动重启中…");
                KillChild();
                TryLaunch();
                // 简单等待一个探测周期，交给 StartIfNeeded 的完整逻辑？不：这里只做轻量重拉
                // 完整重试逻辑在 StartIfNeeded，但监控线程不阻塞 UI，用异步重试：
                ThreadPool.QueueUserWorkItem(delegate { RetryToReady(); });
            }
            catch { }
        }

        private void RetryToReady()
        {
            for (int i = 0; i < _cfg.WaitSeconds; i++)
            {
                Thread.Sleep(1000);
                if (_disposed) return;
                if (IsDshServing())
                {
                    SetStatus(ServiceStatus.Online, "服务已自动恢复");
                    OwnsProcess = true;
                    return;
                }
            }
            SetStatus(ServiceStatus.Failed, "自动重启后仍未就绪：" + (LastError ?? "未知错误"));
        }

        // =============== 状态与生命周期 ===============

        private void SetStatus(ServiceStatus status, string detail)
        {
            Status = status;
            Detail = detail;
            if (StateChanged != null)
            {
                try { StateChanged(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_watchTimer != null) _watchTimer.Dispose(); } catch { }
            KillChild();
        }
    }
}

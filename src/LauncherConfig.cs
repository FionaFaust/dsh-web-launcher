using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace DshWebLauncher
{
    /// <summary>
    /// 启动器配置。读取 exe 同目录 launcher.config.json；文件缺失或字段缺失时使用默认值。
    /// 未知字段忽略（向前兼容），新增字段缺失时用默认值（向后兼容）。
    /// </summary>
    internal sealed class LauncherConfig
    {
        public string Title = "Euporiandra's DeepSeek Harness Web Launcher";
        public string HomeUrl = "http://127.0.0.1:3080";
        public int Port = 3080;
        public string StartScript = "";
        public int WaitSeconds = 40;
        public double WindowScale = 0.72;
        public bool AutoHideToolbar = true;
        public Size[] Resolutions = new Size[] { new Size(3840, 2160), new Size(2560, 1600) };

        // ---- ver1.1.5.0 新增 ----
        /// <summary>dsh web 工作目录（空 = exe 所在目录）。</summary>
        public string WorkspaceRoot = "";
        /// <summary>服务启动成功后挂掉是否自动重启（兜底措施）。</summary>
        public bool AutoRestart = true;
        /// <summary>启动失败最大重试次数（兜底措施）。</summary>
        public int MaxStartRetries = 3;
        /// <summary>是否落盘日志到 exe 目录 logs\launcher.log。</summary>
        public bool EnableLog = true;

        /// <summary>从 exe 同目录加载配置。</summary>
        public static LauncherConfig Load()
        {
            LauncherConfig cfg = new LauncherConfig();
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "launcher.config.json");
                if (!File.Exists(path))
                {
                    WriteDefault(path, cfg);
                    return cfg;
                }

                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> dict = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                if (dict == null) return cfg;

                object v;
                if (dict.TryGetValue("title", out v)) cfg.Title = Convert.ToString(v);
                if (dict.TryGetValue("url", out v)) cfg.HomeUrl = Convert.ToString(v);
                if (dict.TryGetValue("port", out v))
                {
                    int p = Convert.ToInt32(v);
                    if (p > 0 && p <= 65535) cfg.Port = p;
                }
                if (dict.TryGetValue("startScript", out v)) cfg.StartScript = Convert.ToString(v);
                if (dict.TryGetValue("waitSeconds", out v))
                {
                    int w = Convert.ToInt32(v);
                    if (w > 0) cfg.WaitSeconds = w;
                }
                if (dict.TryGetValue("windowScale", out v))
                {
                    double s = Convert.ToDouble(v);
                    if (s >= 0.1 && s <= 1.0) cfg.WindowScale = s;
                }
                if (dict.TryGetValue("autoHideToolbar", out v)) cfg.AutoHideToolbar = Convert.ToBoolean(v);
                if (dict.TryGetValue("workspaceRoot", out v)) cfg.WorkspaceRoot = Convert.ToString(v);
                if (dict.TryGetValue("autoRestart", out v)) cfg.AutoRestart = Convert.ToBoolean(v);
                if (dict.TryGetValue("maxStartRetries", out v))
                {
                    int m = Convert.ToInt32(v);
                    if (m >= 0 && m <= 10) cfg.MaxStartRetries = m;
                }
                if (dict.TryGetValue("enableLog", out v)) cfg.EnableLog = Convert.ToBoolean(v);
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
                        if (list.Count > 0) cfg.Resolutions = list.ToArray();
                    }
                }
            }
            catch { /* 配置损坏时全部使用默认值 */ }
            return cfg;
        }

        /// <summary>首次运行生成默认配置文件（UTF-8 无 BOM）。</summary>
        private static void WriteDefault(string path, LauncherConfig cfg)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"title\": " + JsonStr(cfg.Title) + ",");
                sb.AppendLine("  \"url\": " + JsonStr(cfg.HomeUrl) + ",");
                sb.AppendLine("  \"port\": " + cfg.Port + ",");
                sb.AppendLine("  \"startScript\": " + JsonStr(cfg.StartScript) + ",");
                sb.AppendLine("  \"waitSeconds\": " + cfg.WaitSeconds + ",");
                sb.AppendLine("  \"windowScale\": " + cfg.WindowScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ",");
                sb.AppendLine("  \"autoHideToolbar\": " + (cfg.AutoHideToolbar ? "true" : "false") + ",");
                sb.AppendLine("  \"workspaceRoot\": " + JsonStr(cfg.WorkspaceRoot) + ",");
                sb.AppendLine("  \"autoRestart\": " + (cfg.AutoRestart ? "true" : "false") + ",");
                sb.AppendLine("  \"maxStartRetries\": " + cfg.MaxStartRetries + ",");
                sb.AppendLine("  \"enableLog\": " + (cfg.EnableLog ? "true" : "false") + ",");
                sb.Append("  \"resolutions\": [");
                for (int i = 0; i < cfg.Resolutions.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append("[" + cfg.Resolutions[i].Width + ", " + cfg.Resolutions[i].Height + "]");
                }
                sb.AppendLine("]");
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
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
    }
}

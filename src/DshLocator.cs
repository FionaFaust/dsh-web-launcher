using System;
using System.IO;

namespace DshWebLauncher
{
    /// <summary>
    /// 定位本机可用的 dsh CLI 与 node.exe。
    /// 探测顺序：显式配置 → PATH → npx 缓存 → 全局 npm 安装。
    /// ver1.1.5.0：比原版多覆盖 npx 缓存与全局 npm，并可从 dsh.cmd shim 解析出 bin.js 直启。
    /// </summary>
    internal static class DshLocator
    {
        /// <summary>查找 dsh 命令入口（dsh.cmd / dsh.bat / dsh.exe / dsh）。找不到返回 null。</summary>
        public static string FindDshCli()
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

        /// <summary>
        /// 兜底：从 dsh shim（PATH 上的 dsh.cmd）解析出 @deepseek-ai/dsh/lib/bin.js 直启。
        /// shim 位于 ...\node_modules\.bin\，bin.js 在 ...\node_modules\@deepseek-ai\dsh\lib\bin.js
        /// </summary>
        public static string ResolveBinJsFromShim(string shimPath)
        {
            try
            {
                string binDir = Path.GetDirectoryName(shimPath);
                if (binDir == null) return null;
                string parent = Path.GetDirectoryName(binDir);
                if (parent == null) return null;
                string cand = Path.Combine(parent, "@deepseek-ai", "dsh", "lib", "bin.js");
                return File.Exists(cand) ? cand : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 兜底：不依赖 PATH，直接定位 @deepseek-ai/dsh/lib/bin.js。
        /// 顺序：npx 缓存 → 全局 npm。
        /// </summary>
        public static string FindDshBinJsDirect()
        {
            try
            {
                // npx 缓存：%LOCALAPPDATA%\npm-cache\_npx\<hash>\node_modules\@deepseek-ai\dsh\lib\bin.js
                string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string cacheRoot = Path.Combine(la, "npm-cache", "_npx");
                if (Directory.Exists(cacheRoot))
                {
                    foreach (string npxDir in Directory.EnumerateDirectories(cacheRoot))
                    {
                        string cand = Path.Combine(npxDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                        if (File.Exists(cand)) return cand;
                    }
                }

                // 全局 npm：%APPDATA%\npm\node_modules
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string cand2 = Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                if (File.Exists(cand2)) return cand2;

                // 常见本地安装
                string[] extra = {
                    @"C:\Users\Lenovo\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh\lib\bin.js",
                };
                foreach (string c in extra)
                {
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }

        /// <summary>查找 node.exe：PATH → 常见安装位置。找不到返回 null。</summary>
        public static string FindNode()
        {
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dirRaw in pathEnv.Split(';'))
                {
                    string dir = dirRaw.Trim();
                    if (dir.Length == 0) continue;
                    string cand = Path.Combine(dir, "node.exe");
                    if (File.Exists(cand)) return cand;
                }
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] candidates = {
                    Path.Combine(pf, "nodejs", "node.exe"),
                    Path.Combine(pf86, "nodejs", "node.exe"),
                    Path.Combine(la, "Programs", "nodejs", "node.exe"),
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
}

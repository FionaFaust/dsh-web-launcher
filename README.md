# DSH Web Launcher

> DeepSeek Harness 的内嵌浏览器启动器 —— 无浏览器 UI 的 WebView2 (Chromium) 客户端，单文件绿色运行。

[![License: BSD-2-Clause](https://img.shields.io/badge/License-BSD--2--Clause-blue.svg)](LICENSE)

把 DeepSeek Harness 的 Web 界面（`http://127.0.0.1:3080`）装进一个自带窗口的小程序里：不打开 Edge/Chrome，不显示地址栏、标签页等浏览器界面，但浏览功能与 Chromium 完全一致（登录、对话、工具调用、文件上传、F12 开发者工具全部可用）。

## 功能特性

- 🐋 **内嵌浏览器**：基于 Microsoft Edge WebView2（Chromium 内核），页面直接渲染在程序窗口内，不唤起任何外部浏览器
- 🪟 **无浏览器 UI**：只有极简导航条（后退/前进/刷新/主页 + URL 显示）与状态栏，没有浏览器外壳
- 🔍 **两种渲染模式**：
  - `跟随窗口（推荐）`：页面按窗口实际尺寸 + 系统 DPI 渲染，文字清晰锐利
  - `固定分辨率`：锁定渲染分辨率（如 3840×2160 4K / 2560×1600），窗口只是缩放取景框，调整窗口大小不影响页面视口
- 🖥️ **F11 全屏 / Esc 退出**：一键进入沉浸式全屏，再按 Esc 精确恢复窗口
- 🚀 **自动拉起服务**：目标端口未就绪时，自动隐藏调用配置的启动脚本并等待就绪（可关闭）
- 📄 **单文件绿色运行**：exe 内嵌 WebView2 托管程序集，首次运行自动释放原生 Loader，无需安装
- ⚙️ **JSON 配置**：标题、URL、端口、启动脚本、窗口比例、分辨率档位全部可配置

## 运行环境

| 依赖 | 说明 |
|---|---|
| Windows 10 / 11 | x64 |
| Microsoft Edge WebView2 Runtime | Win11 自带；Win10 可在 [Microsoft 官网](https://developer.microsoft.com/microsoft-edge/webview2/) 获取 |
| .NET Framework 4.8 | Win10/11 系统自带 |

## 快速开始

1. 从 Releases 下载 `DSHWebLauncher.exe`（单文件）
2. （可选）在 exe 同目录创建 `launcher.config.json` 进行配置，见下文
3. 双击运行。若目标服务未启动且配置了 `startScript`，程序会自动拉起并等待就绪，然后加载页面

> 提示：exe 首次运行时会在同目录生成 `WebView2Loader.dll`（原生组件），属正常行为，请勿删除。

## 配置

程序读取 exe 同目录的 `launcher.config.json`（UTF-8 编码）。文件不存在或字段缺失时使用默认值。完整示例见 [`launcher.config.example.json`](launcher.config.example.json)。

```jsonc
{
  "title": "DeepSeek Harness Web",          // 窗口标题（默认 "DeepSeek Harness Web"）
  "url": "http://127.0.0.1:3080",           // 目标页面地址
  "port": 3080,                             // 端口就绪检测端口（与 url 对应）
  "startScript": "",                        // 可选：服务未运行时隐藏调用的启动脚本
                                            //   （如 start-dsh.cmd；留空则不自动拉起）
  "waitSeconds": 40,                        // 等待服务就绪的最长秒数
  "windowScale": 0.72,                      // 初始窗口 = 屏幕尺寸 × 该比例（0.1~1.0）
  "resolutions": [[3840, 2160], [2560, 1600]]  // 固定分辨率档位（导航条下拉可选）
}
```

示例（自动拉起自建脚本 + 4K 档位）：

```json
{
  "title": "My DSH",
  "url": "http://127.0.0.1:3080",
  "port": 3080,
  "startScript": "D:\\scripts\\start-dsh.cmd",
  "waitSeconds": 60,
  "windowScale": 0.8,
  "resolutions": [[3840, 2160], [2560, 1600], [1920, 1080]]
}
```

## 快捷键

| 按键 | 功能 |
|---|---|
| `F11` | 进入全屏（隐藏导航条/状态栏，覆盖所在显示器） |
| `Esc` | 退出全屏（恢复原窗口大小与位置） |

## 从源码构建

需要：Windows + .NET Framework 4.8 SDK（`csc.exe`，系统自带）。

```bat
git clone https://github.com/FionaFaust/dsh-web-launcher.git
cd dsh-web-launcher
build.cmd
```

产物输出到 `dist\DSHWebLauncher.exe`。

### WebView2 SDK 说明

`vendor/` 下已内置编译所需的 WebView2 SDK 程序集（MIT 许可，来源：`Microsoft.Web.WebView2` NuGet 包 v1.0.2535.41）：

| 文件 | 用途 |
|---|---|
| `Microsoft.Web.WebView2.Core.dll` | WebView2 托管 API（运行时从 exe 资源加载） |
| `Microsoft.Web.WebView2.WinForms.dll` | WinForms 控件（运行时从 exe 资源加载） |
| `WebView2Loader.dll` | 原生加载器（首次运行自动释放到 exe 目录） |

### 自定义图标

- 默认使用 `assets/whale.ico`（DeepSeek Harness 官方鲸鱼图标，SVG 源见 `assets/favicon.svg`）
- 如需替换：在 exe 同目录放一个 `app.ico`，程序会优先使用它（无需重新编译）

## 目录结构

```
dsh-web-launcher/
├── src/
│   ├── App.cs              # 主程序（配置加载 / 窗口 / WebView2）
│   ├── app.manifest        # DPI 感知清单
│   └── IconBuilder.cs      # ICO 生成工具（assets 用，可选）
├── assets/
│   ├── whale.ico           # 默认鲸鱼图标（多尺寸 16/32/48/256）
│   └── favicon.svg         # 图标 SVG 源（来自 DSH Web 官方）
├── vendor/                 # WebView2 SDK 程序集（MIT）
├── launcher.config.example.json
├── build.cmd
└── LICENSE                 # BSD-2-Clause
```

## 常见问题

**Q：页面打不开 / 一直"等待服务启动"**
A：确认目标服务已运行且端口正确；检查 `launcher.config.json` 的 `url`/`port`；若配置了 `startScript`，检查脚本路径与脚本本身是否能正常拉起服务。

**Q：提示"无法初始化内嵌浏览器"**
A：确认系统已安装 WebView2 Runtime（Win11 自带）。错误信息中会包含具体原因。

**Q：调整窗口大小后页面布局变了**
A：当前是"跟随窗口"模式（推荐，清晰）。若想锁定布局，在导航条下拉中选择"固定分辨率"档位。

**Q：exe 目录下多了一个 `WebView2Loader.dll`**
A：这是程序首次运行自动释放的原生组件，属于正常行为；删除后下次运行会自动重新释放。

## 致谢

- [DeepSeek Harness](https://github.com/deepseek-ai/dsh) —— 本项目服务的对象
- Microsoft Edge WebView2 —— 内嵌浏览器内核
- 鲸鱼图标来自 DeepSeek Harness Web 官方资源

## 许可证

[BSD 2-Clause License](LICENSE)

Copyright © 2026 艾珀莉亚 (FionaFaust)

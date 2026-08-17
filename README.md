# DSH Web Launcher 🐋

> 给 DeepSeek Harness 套个"小窗户"的启动器 —— 不开浏览器、没有地址栏，干干净净一个窗口里就是你的 DSH。

[![License: BSD-2-Clause](https://img.shields.io/badge/License-BSD--2--Clause-blue.svg)](LICENSE)

## 这是个啥？

简单说：你平时得用浏览器打开 `http://127.0.0.1:3080` 才能进 DSH，可浏览器那一堆标签页、地址栏、菜单，你其实根本用不上对吧？

这个程序就是把 DSH 的网页"装进"自己的小窗口里：

- ✅ 不打开 Edge / Chrome，不弹浏览器
- ✅ 没有地址栏、标签页、书签栏这些用不上的东西
- ✅ 但浏览能力一模一样——登录、聊天、工具调用、传文件、按 F12 调开发者工具，全都行（毕竟里面是真·Chromium 内核）

一句话：**一个自带鲸鱼图标的、干干净净的 DSH 专属小窗口。**

## 它有什么本事？

- 🐋 **鲸鱼图标**：默认就是 DSH 官方的黑鲸鱼，深色系爱好者狂喜
- 🪟 **极简界面**：顶部只有 后退/前进/刷新/主页 + 一个分辨率下拉 + 网址显示，下面全是页面
- 🔍 **两种显示模式**（导航条上随便切）：
  - `跟随窗口`（推荐）：窗口多大页面就多大，按系统 DPI 渲染，**字清晰不模糊**
  - `固定分辨率`：把渲染分辨率锁死（比如 4K / 2560×1600），窗口只是"取景框"，怎么拉都不影响页面布局
- 🖥️ **F11 秒进全屏**，再按一下 **Esc** 优雅退出，窗口大小位置原样回来
- 🚀 **帮你喊醒服务**：如果 DSH 没在运行，它可以自动调用启动脚本（配置里指定），等就绪了再加载页面
- 📦 **单文件绿色运行**：整个程序就一个 exe，不需要安装，拷走就能用
- ⚙️ **啥都能配置**：窗口标题、目标地址、端口、启动脚本、窗口大小、分辨率档位……都写在一个小 JSON 里

## 需要什么环境？

- Windows 10 / 11（x64）
- WebView2 Runtime（Win11 自带；Win10 去 [微软官网](https://developer.microsoft.com/microsoft-edge/webview2/) 装一个）
- .NET Framework 4.8（系统自带的，不用管）

## 怎么用？

1. 下载 `DSHWebLauncher.exe`（单文件，就这一个）
2. 想改配置的话，在 exe 旁边放一个 `launcher.config.json`（模板见下）
3. 双击，完事。

> 小提醒：第一次运行 exe 旁边会多个 `WebView2Loader.dll`，这是程序自动放出来的原生组件，别删它，删了下次也会自己再放。

## 配置说明（就是那个 JSON）

配置文件要和 exe 放一起，名字必须是 `launcher.config.json`（UTF-8 编码）。不写也没关系，全都有默认值。

```json
{
  "title": "DeepSeek Harness Web",
  "url": "http://127.0.0.1:3080",
  "port": 3080,
  "startScript": "",
  "waitSeconds": 40,
  "windowScale": 0.72,
  "resolutions": [[3840, 2160], [2560, 1600]]
}
```

| 字段 | 干嘛的 | 默认值 |
|---|---|---|
| `title` | 窗口标题栏写什么 | `DeepSeek Harness Web` |
| `url` | 要打开哪个页面 | `http://127.0.0.1:3080` |
| `port` | 检测"服务活着没"用的端口（和 url 对应） | `3080` |
| `startScript` | 服务没启动时，悄悄调用这个脚本来启动（留空 = 不自动启动） | `""` |
| `waitSeconds` | 等服务就绪最多等多少秒 | `40` |
| `windowScale` | 开窗时多大 = 屏幕尺寸 × 这个比例（0.1~1.0） | `0.72` |
| `resolutions` | 固定分辨率下拉里提供哪些档位 | 4K 和 2560×1600 |

举个"带启动脚本"的完整例子：

```json
{
  "title": "我的 DSH",
  "url": "http://127.0.0.1:3080",
  "port": 3080,
  "startScript": "D:\\scripts\\start-dsh.cmd",
  "waitSeconds": 60,
  "windowScale": 0.8,
  "resolutions": [[3840, 2160], [2560, 1600], [1920, 1080]]
}
```

## 快捷键

| 按键 | 效果 |
|---|---|
| `F11` | 全屏（导航条状态栏统统藏起来，画面铺满屏幕） |
| `Esc` | 退出全屏（原来的窗口大小位置分毫不差地回来） |

## 自己动手编译

环境：Windows + .NET Framework 4.8 SDK（csc.exe，系统自带）。

```bat
git clone https://github.com/FionaFaust/dsh-web-launcher.git
cd dsh-web-launcher
build.cmd
```

编译完的 exe 在 `dist\DSHWebLauncher.exe`。

### 关于 vendored 的 WebView2 SDK

`vendor/` 里放的三个 DLL 来自微软的 `Microsoft.Web.WebView2` NuGet 包 v1.0.2535.41，MIT 许可，可以放心重新分发，版权声明见 `vendor/LICENSE-WebView2-MIT.txt`。

- `Microsoft.Web.WebView2.Core.dll` / `WinForms.dll`：编译和运行时都要用（运行时从 exe 里直接加载）
- `WebView2Loader.dll`：原生加载器，首次运行自动释放到 exe 旁边

### 想换图标？

默认用 `assets/whale.ico`（DSH 官方鲸鱼，SVG 源在 `assets/favicon.svg`）。
不想重新编译也可以：在 exe 旁边放一个 `app.ico`，程序会优先用你的。

## 仓库结构速览

```
dsh-web-launcher/
├── src/
│   ├── App.cs              # 主程序：配置、窗口、WebView2 全在这
│   ├── app.manifest        # DPI 感知声明（清晰渲染的秘诀）
│   └── IconBuilder.cs      # 做图标的小工具（你基本用不上）
├── assets/
│   ├── whale.ico           # 默认鲸鱼图标（16/32/48/256 四尺寸）
│   └── favicon.svg         # 图标 SVG 原图（来自 DSH Web）
├── vendor/                 # WebView2 SDK（MIT，附版权声明）
├── launcher.config.example.json   # 配置模板
├── build.cmd               # 一键编译
└── LICENSE                 # BSD-2-Clause
```

## 遇到问题？

**Q：页面打不开，一直显示"等待服务启动"**
A：先确认 DSH 真的在跑、端口对不对；再看看 `launcher.config.json` 里 `url` / `port` 是不是写对了。配了 `startScript` 的话，检查脚本路径和脚本本身能不能正常把服务拉起来。

**Q：提示"无法初始化内嵌浏览器"**
A：多半是没装 WebView2 Runtime（Win11 自带，Win10 去官网装）。报错信息里会写具体原因。

**Q：拉窗口大小，页面布局也跟着变了？**
A：你现在是"跟随窗口"模式（默认，字最清晰）。想要布局锁死，就在导航条下拉里选一个"固定分辨率"档位。

**Q：exe 旁边那个 `WebView2Loader.dll` 是啥？能删吗？**
A：程序自动释放的原生组件，别删，删了下次运行还会自己放出来。

## 致谢

- [DeepSeek Harness](https://github.com/deepseek-ai/dsh) —— 这个窗口服务的对象
- Microsoft Edge WebView2 —— 内核老大哥
- 鲸鱼图标：DSH Web 官方资源

## 许可证

[BSD 2-Clause License](LICENSE)

Copyright © 2026 艾珀莉亚 (FionaFaust)

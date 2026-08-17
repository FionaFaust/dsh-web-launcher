@echo off
setlocal
rem ============================================================
rem  DSH Web Launcher - build script
rem  Requires: Windows 10/11, .NET Framework 4.8 SDK (csc.exe)
rem  Output:   dist\DSHWebLauncher.exe  (single-file, self-contained)
rem ============================================================

set "ROOT=%~dp0"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. .NET Framework 4.8 SDK is required.
    exit /b 1
)

if not exist "%ROOT%vendor\Microsoft.Web.WebView2.Core.dll" (
    echo [ERROR] Missing vendor SDK files. See README.md "Build from source".
    exit /b 1
)

if not exist "%ROOT%dist" mkdir "%ROOT%dist"

echo [1/2] Compiling...
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 ^
    /win32manifest:"%ROOT%src\app.manifest" ^
    /win32icon:"%ROOT%assets\whale.ico" ^
    /out:"%ROOT%dist\DSHWebLauncher.exe" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
    /r:"%ROOT%vendor\Microsoft.Web.WebView2.Core.dll" ^
    /r:"%ROOT%vendor\Microsoft.Web.WebView2.WinForms.dll" ^
    /resource:"%ROOT%vendor\Microsoft.Web.WebView2.Core.dll",Microsoft.Web.WebView2.Core.dll ^
    /resource:"%ROOT%vendor\Microsoft.Web.WebView2.WinForms.dll",Microsoft.Web.WebView2.WinForms.dll ^
    /resource:"%ROOT%vendor\WebView2Loader.dll",WebView2Loader.dll ^
    /resource:"%ROOT%assets\whale.ico",whale.ico ^
    "%ROOT%src\App.cs"

if errorlevel 1 (
    echo [ERROR] Compilation failed.
    exit /b 1
)

echo [2/2] Copying config example...
if not exist "%ROOT%dist\launcher.config.example.json" (
    copy /Y "%ROOT%launcher.config.example.json" "%ROOT%dist\launcher.config.example.json" >nul
)

echo.
echo Done. Output: %ROOT%dist\DSHWebLauncher.exe
echo Copy launcher.config.example.json to launcher.config.json next to the exe to customize.
endlocal

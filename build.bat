@echo off
chcp 65001 >nul
cd /d "%~dp0"
taskkill /im CDPlayer.exe /f >nul 2>&1
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:CDPlayer.exe /win32icon:CDPlayer.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%WM%\Windows.Media.winmd" /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Storage.winmd" /r:"%FW%\System.Runtime.dll" CDPlayer.cs
if errorlevel 1 (echo 编译失败 & exit /b 1)
echo 编译成功: CDPlayer.exe

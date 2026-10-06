@echo off
setlocal
cd /d "%~dp0"
echo Compiling Google Flow Desktop (C# .NET)...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /platform:x64 /out:GoogleFlowDesktop.exe /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Web.Extensions.dll,System.Net.Http.dll,System.Management.dll,Microsoft.Web.WebView2.Core.dll,Microsoft.Web.WebView2.WinForms.dll src\GpuConfig.cs src\UnlockerScript.cs src\TaskEngine.cs src\Notifier.cs src\WorkerDispatcher.cs src\LocalServer.cs src\UpdateManager.cs src\MainForm.cs src\Program.cs
if %ERRORLEVEL% EQU 0 (
    echo Compilation successful: GoogleFlowDesktop.exe created!
) else (
    echo Compilation failed with error code %ERRORLEVEL%
)

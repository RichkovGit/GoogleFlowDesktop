@echo off
setlocal
cd /d "%~dp0"
echo Compiling Google Flow Desktop (native C# / WinForms)...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:GoogleFlowDesktop.exe ^
  /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Web.Extensions.dll,System.Net.Http.dll,System.Management.dll,Microsoft.Web.WebView2.Core.dll,Microsoft.Web.WebView2.WinForms.dll ^
  src\Theme.cs src\ControlsExtra.cs src\ClipboardUtil.cs src\AppIcon.cs src\PromptKit.cs ^
  src\GpuConfig.cs src\UnlockerScript.cs src\Notifier.cs src\NativeStudio.cs src\NativeTelemetry.cs ^
  src\UpdateManager.cs src\UpdateDialog.cs src\MainForm.cs src\Program.cs
if %ERRORLEVEL% EQU 0 (
    echo Compilation successful: GoogleFlowDesktop.exe created!
) else (
    echo Compilation failed with error code %ERRORLEVEL%
)
endlocal

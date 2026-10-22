param([switch]$SelfContained,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
$localSdk = Join-Path $workspace '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$project = Join-Path $workspace 'src\FlowStudio\FlowStudio.csproj'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $workspace 'artifacts\FlowStudio' }
if ($SelfContained) {
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output
} else {
    & $dotnet publish $project -c Release -r win-x64 --self-contained false -o $output
}
if ($LASTEXITCODE -ne 0) { throw 'Сборка Flow Studio не удалась.' }
Copy-Item -LiteralPath (Join-Path $workspace 'README.md') -Destination (Join-Path $output 'README.md') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $output 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'docs\hardware.md'),(Join-Path $workspace 'docs\verification.md'),(Join-Path $workspace 'docs\automation.md') -Destination (Join-Path $output 'docs') -Force
Copy-Item -LiteralPath (Join-Path $workspace 'docs\updates.md'),(Join-Path $workspace 'docs\flow-interface-audit.md') -Destination (Join-Path $output 'docs') -Force
$toolDirectory = Join-Path $output 'tools'
New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
$bundledFfmpeg = Join-Path $workspace 'tools\ffmpeg.exe'
$userFfmpeg = Join-Path $env:USERPROFILE '.local\bin\ffmpeg.exe'
$ffmpegSource = if (Test-Path -LiteralPath $bundledFfmpeg) { $bundledFfmpeg } elseif (Test-Path -LiteralPath $userFfmpeg) { $userFfmpeg } else { $null }
if ($ffmpegSource) { Copy-Item -LiteralPath $ffmpegSource -Destination (Join-Path $toolDirectory 'ffmpeg.exe') -Force }
if (Test-Path -LiteralPath (Join-Path $workspace 'tools\COPYING.GPLv3')) { Copy-Item -LiteralPath (Join-Path $workspace 'tools\COPYING.GPLv3'),(Join-Path $workspace 'tools\FFMPEG-NOTICE.txt') -Destination $toolDirectory -Force }
Write-Host "Готово: $output\FlowStudio.exe"

param([string]$Version='2.0.1',[string]$BuildDirectory)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$build=if($BuildDirectory){[IO.Path]::GetFullPath($BuildDirectory)}else{Join-Path $workspace ('artifacts\FlowStudio-'+$Version)}
if(-not(Test-Path -LiteralPath (Join-Path $build 'FlowStudio.exe'))){throw 'Build the self-contained application first.'}
$release=Join-Path $workspace ('artifacts\release-'+$Version)
New-Item -ItemType Directory -Force -Path $release | Out-Null
$package=Join-Path $release ('FlowStudio-'+$Version+'-win-x64.zip')
if(Test-Path -LiteralPath $package){throw 'A package already exists; choose another output version or preserve it before rebuilding.'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($build,$package,[IO.Compression.CompressionLevel]::Optimal,$false)
$manifest=[ordered]@{Version=$Version;PackageUrl=('https://github.com/RichkovGit/GoogleFlowDesktop/releases/download/v'+$Version+'/'+[IO.Path]::GetFileName($package));Sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash;SizeBytes=(Get-Item -LiteralPath $package).Length;UpdaterProtocol=1}
$manifestPath=Join-Path $release 'FlowStudio-update.json'
[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$keyPath=Join-Path $env:LOCALAPPDATA 'FlowStudioPublisher\update-key.bin'
$clearKey=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
$rsa=[Security.Cryptography.RSA]::Create()
try{
    $read=0;$rsa.ImportPkcs8PrivateKey($clearKey,[ref]$read)
    $signature=$rsa.SignData([IO.File]::ReadAllBytes($manifestPath),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
    [IO.File]::WriteAllBytes((Join-Path $release 'FlowStudio-update.sig'),$signature)
    $public=[IO.File]::ReadAllText((Join-Path $workspace 'src\FlowStudio\Resources\update-public.pem'))
    $verifier=[Security.Cryptography.RSA]::Create();$verifier.ImportFromPem($public)
    if(-not $verifier.VerifyData([IO.File]::ReadAllBytes($manifestPath),$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)){throw 'Publisher key does not match the pinned application key.'}
    $verifier.Dispose()
}finally{[Security.Cryptography.CryptographicOperations]::ZeroMemory($clearKey);$rsa.Dispose()}
Write-Host ('Release package and verified signature: '+$release)

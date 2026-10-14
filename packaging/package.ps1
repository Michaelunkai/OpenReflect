# OpenReflect portable packaging (S35/S36)
$ErrorActionPreference = 'Stop'
$scoopSdk = 'F:\backup\windowsapps\installed\scoop\apps\dotnet-sdk\current'
$env:PATH = "$scoopSdk;$env:PATH"
$env:DOTNET_ROOT = $scoopSdk
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:MSBuildSDKsPath = "$scoopSdk\sdk\10.0.402\Sdks"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
$stage = Join-Path $root 'dist\stage'
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Write-Host '== publish CLI + GUI (win-x64, framework-dependent) =='
dotnet publish src\CLI\OpenReflect.CLI.csproj -c Release -f net10.0 -r win-x64 --self-contained false -o "$stage\cli" --nologo
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
dotnet publish src\GUI\OpenReflect.GUI.csproj -c Release -f net10.0-windows -r win-x64 --self-contained false -o "$stage\gui" --nologo
if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed' }

Copy-Item LICENSE, NOTICE, README.md, CHANGELOG.md, ROADMAP.md $stage -ErrorAction SilentlyContinue
Set-Content "$stage\VERSION" -Value (Get-Date -Format 'yyyy-MM-dd')

$zip = Join-Path $dist 'OpenReflect-0.2.0-win-x64-portable.zip'
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Value "$hash  OpenReflect-0.2.0-win-x64-portable.zip"
Write-Host "== staged ==" 
Get-Item $zip | Select-Object Name, @{n='MB'; e={ [math]::Round($_.Length/1MB,1) }}
Write-Host "SHA256: $hash"

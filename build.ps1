# OpenReflect official build + test gate (PowerShell 5.1 compatible)
# Pins the healthy scoop SDK — the machine-wide dotnet is broken (gutted Sdk folders).
$ErrorActionPreference = 'Stop'
$scoopSdk = 'F:\backup\windowsapps\installed\scoop\apps\dotnet-sdk\current'
if (-not (Test-Path "$scoopSdk\dotnet.exe")) { throw "scoop SDK not found at $scoopSdk" }
$env:PATH = "$scoopSdk;$env:PATH"
$env:DOTNET_ROOT = $scoopSdk
$env:MSBuildSDKsPath = "$scoopSdk\sdk\10.0.401\Sdks"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "== build ==" -ForegroundColor Cyan
dotnet build OpenReflect.slnx -c Debug --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

Write-Host "== test ==" -ForegroundColor Cyan
dotnet test tests\OpenReflect.Tests\OpenReflect.Tests.csproj --no-build --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "TESTS FAILED" -ForegroundColor Red; exit 1 }

Write-Host "ALL GREEN" -ForegroundColor Green
exit 0

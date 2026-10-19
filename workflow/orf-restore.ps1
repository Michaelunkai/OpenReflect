# orf-restore.ps1 - OpenReflect system restore armer (rmback/macres parity)
# Picks the newest .orf in F:\win11backup, verifies it in full, preflights the
# restore plan, ensures the offline WinRE environment exists, writes
# restore-task.json and arms a ONE-SHOT boot entry (bcdedit /bootsequence).
# This script NEVER reboots. The restore itself runs in the offline environment
# at next boot; the bootsequence entry is one-shot and self-expiring.
#
# usage:
#   orf-restore.ps1              verify + preflight + arm (prints instructions, no reboot)
#   orf-restore.ps1 -PlanOnly    verify + preflight + plan report, arms nothing
#   orf-restore.ps1 -Disarm      remove one-shot boot entry and restore-task.json
#   orf-restore.ps1 -SafeWrite   arm with zero-fill of skipped regions (slower, cleans slack)
#   orf-restore.ps1 -Force       re-arm even if a task file already exists
#
# NEVER touches F:\win11recovery (the Macrium folder).

param(
    [switch]$PlanOnly,
    [switch]$Disarm,
    [switch]$SafeWrite,
    [switch]$Force,
    [string]$DestDir = 'F:\win11backup',
    [string]$CliPath = 'F:\backup\windowsapps\installed\OpenReflect\cli\OpenReflect.CLI.exe',
    [string]$EnvDir = 'C:\OpenReflectRestoreEnv'
)

$ErrorActionPreference = 'Stop'
$armGuidFile = Join-Path $DestDir 'arm-bootsequence.guid'

# ---- admin check -----------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'orf-restore: administrator rights are required (bcdedit + verify).' -ForegroundColor Red
    exit 2
}

# ---- disarm ----------------------------------------------------------------
if ($Disarm) {
    if (Test-Path $armGuidFile) {
        $guid = (Get-Content $armGuidFile -Raw).Trim()
        if ($guid) {
            bcdedit /delete $guid | Out-Null
            Write-Host "orf-restore: removed one-shot boot entry $guid"
        }
        Remove-Item $armGuidFile -Force
    } else { Write-Host 'orf-restore: nothing armed (no guid file).' }
    if (Test-Path (Join-Path $DestDir 'restore-task.json')) {
        Remove-Item (Join-Path $DestDir 'restore-task.json') -Force
        Write-Host 'orf-restore: deleted restore-task.json.'
    }
    exit 0
}

if (-not (Test-Path $CliPath)) { Write-Host "orf-restore: CLI not found at $CliPath" -ForegroundColor Red; exit 2 }

# ---- newest image ----------------------------------------------------------
$images = @(Get-ChildItem -LiteralPath $DestDir -Filter '*.orf' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending)
if ($images.Count -eq 0) { Write-Host "orf-restore: no .orf images found in $DestDir" -ForegroundColor Red; exit 2 }
$image = $images[0].FullName
Write-Host ("orf-restore: newest image = {0} ({1:N2} GiB, {2})" -f $image, ($images[0].Length / 1GB), $images[0].LastWriteTime)

# ---- mandatory full verify --------------------------------------------------
Write-Host 'orf-restore: running MANDATORY full verify (this re-reads and hashes every chunk)...' -ForegroundColor Cyan
& $CliPath verify $image --depth full
if ($LASTEXITCODE -ne 0) { Write-Host 'orf-restore: VERIFY FAILED - refusing to arm.' -ForegroundColor Red; exit 3 }

# ---- target = disk of C: ----------------------------------------------------
$sysDisk = (Get-Partition -DriveLetter $env:SystemDrive.TrimEnd(':')).DiskNumber
$diskInfo = Get-Disk -Number $sysDisk
Write-Host ("orf-restore: target = disk {0} ({1:N1} GiB)" -f $sysDisk, ($diskInfo.Size / 1GB))

# ---- plan preflight (no writes) ---------------------------------------------
# --offline matches how the restore actually runs (offline WinRE via the task
# file); without it the CLI refuses to plan a live-OS target and arming from
# the running OS would be impossible.
$preflight = & $CliPath restore-disk --image $image --disk $sysDisk --plan --offline 2>&1
$preflight | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { Write-Host 'orf-restore: PLAN/PREFLIGHT FAILED - refusing to arm.' -ForegroundColor Red; exit 3 }

if ($PlanOnly) {
    Write-Host 'orf-restore: -PlanOnly - verify + preflight passed; nothing was armed.'
    exit 0
}

# ---- offline environment must exist -----------------------------------------
$envWim = Join-Path $EnvDir 'orwinre.wim'
if (-not (Test-Path $envWim)) {
    Write-Host ("orf-restore: offline restore environment missing ({0})." -f $envWim) -ForegroundColor Red
    Write-Host 'orf-restore: build it first (WinRE injection step); refusing to arm.' -ForegroundColor Red
    exit 4
}

# ---- write task file ---------------------------------------------------------
# resolve the volume-GUID path of the destination drive (letters may shift in WinRE)
$destLetter = $DestDir.Substring(0, 1)
$destVolume = (Get-Volume -DriveLetter $destLetter -ErrorAction SilentlyContinue).Path
if (-not $destVolume) {
    $destVolume = (Get-Partition -DriveLetter $destLetter).AccessPaths |
        Where-Object { $_ -like '\\?\Volume*' } | Select-Object -First 1
}
$relImage = $image.Substring(3)   # strip "X:\" -> "win11backup\xxx.orf"
$imageOnVolume = $destVolume.TrimEnd('\') + '\' + $relImage

$taskJson = @{
    image         = $imageOnVolume
    disk          = $sysDisk
    skipUnwritten = (-not $SafeWrite)
    verifyAfter   = $true
    offline       = $true
} | ConvertTo-Json
$taskPath = Join-Path $DestDir 'restore-task.json'
if ((Test-Path $taskPath) -and -not $Force) {
    Write-Host 'orf-restore: restore-task.json already exists (already armed?). Use -Force to re-arm.' -ForegroundColor Yellow
    exit 5
}
Set-Content $taskPath $taskJson -Encoding Ascii
Write-Host ("orf-restore: wrote {0}:" -f $taskPath)
Write-Host $taskJson

# ---- arm one-shot boot --------------------------------------------------------
# the actual osloader guid lives in the env state file written at setup time
$stateFile = Join-Path $EnvDir 'env-state.json'
if (-not (Test-Path $stateFile)) { Write-Host "orf-restore: missing $stateFile" -ForegroundColor Red; exit 4 }
$envState = Get-Content $stateFile -Raw | ConvertFrom-Json
$loaderGuid = $envState.bootEntryGuid
bcdedit /bootsequence $loaderGuid | Out-Null
$loaderGuid | Set-Content $armGuidFile -Encoding Ascii

Write-Host ''
Write-Host '============================================================' -ForegroundColor Yellow
Write-Host 'ARMED - restart when ready. On the next boot the OpenReflect' -ForegroundColor Yellow
Write-Host 'offline environment will restore the verified image to disk' -ForegroundColor Yellow
Write-Host "$sysDisk automatically, then wait for a keypress before rebooting." -ForegroundColor Yellow
Write-Host 'This script does NOT reboot. The arm is one-shot: after one' -ForegroundColor Yellow
Write-Host 'boot (used or skipped via ESC) Windows boots normally again.' -ForegroundColor Yellow
Write-Host 'To cancel before restarting:  orf-restore.ps1 -Disarm' -ForegroundColor Yellow
Write-Host '============================================================' -ForegroundColor Yellow
exit 0

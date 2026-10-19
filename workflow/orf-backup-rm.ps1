# orf-backup-rm.ps1 - OpenReflect full backup with keep-only-latest cleanup
#
# Always takes a FULL image of the disk that hosts C: into $DestDir
# (never incremental - after the run everything else in $DestDir is deleted,
# so the survivor must be self-contained). A MANDATORY `verify <image> --depth full`
# must pass BEFORE any deletion. Only after verify OK, every OTHER item in
# $DestDir is deleted (old .orf images, logs contents, restore-task.json,
# arm-bootsequence.guid, leftovers), leaving exactly the new .orf.
#
# usage:
#   orf-backup-rm.ps1              real full backup + verify + cleanup
#   orf-backup-rm.ps1 -PlanOnly    show what would run / be deleted, touch nothing
#
# NEVER touches F:\win11recovery; never recurses outside $DestDir.
# PS 5.1 only. Real-time progress streams from the CLI's ##PROGRESS lines.

param(
    [switch]$PlanOnly,
    [string]$DestDir = 'F:\win11backup',
    [string]$CliPath = 'F:\backup\windowsapps\installed\OpenReflect\cli\OpenReflect.CLI.exe'
)

$ErrorActionPreference = 'Stop'
$scriptName = 'orf-backup-rm'

# ---- admin check (same style as orf-restore.ps1) ---------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $PlanOnly) {
    Write-Host "$scriptName`: administrator rights are required (VSS + raw disk read)." -ForegroundColor Red
    exit 2
}

# ---- single-writer mutex ---------------------------------------------------
$mutex = New-Object System.Threading.Mutex($false, 'Global\OpenReflectBackupWriter')
$got = $false
try { $got = $mutex.WaitOne(0) } catch { $got = $false }
if (-not $got) {
    Write-Host "$scriptName`: another backup is already running (Global\OpenReflectBackupWriter held)." -ForegroundColor Red
    exit 2
}

try {
    if (-not (Test-Path -LiteralPath $CliPath)) { Write-Host "$scriptName`: CLI not found at $CliPath" -ForegroundColor Red; exit 2 }
    if (-not (Test-Path -LiteralPath $DestDir)) { New-Item -ItemType Directory -Path $DestDir | Out-Null }

    # ---- source = the disk that hosts C: ------------------------------------
    $sysDisk = (Get-Partition -DriveLetter $env:SystemDrive.TrimEnd(':')).DiskNumber
    $diskInfo = Get-Disk -Number $sysDisk
    Write-Host ("{0}: system disk = disk {1} ({2}, {3:N1} GiB)" -f $scriptName, $sysDisk, $diskInfo.FriendlyName, ($diskInfo.Size / 1GB))

    # ---- full-image name, collision refusal ---------------------------------
    $rand = -join ((48..57) + (97..122) | Get-Random -Count 6 | ForEach-Object { [char]$_ })
    $name = 'backup_full_{0}_{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $rand
    $dest = Join-Path $DestDir "$name.orf"
    if (Test-Path -LiteralPath $dest) { Write-Host "$scriptName`: name collision on $dest - refusing." -ForegroundColor Red; exit 2 }

    # ---- cleanup plan: every item in DestDir except the new image -----------
    $destDirRoot = (Get-Item -LiteralPath $DestDir).FullName.TrimEnd('\')
    $othersBefore = @(Get-ChildItem -LiteralPath $DestDir -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $dest })

    if ($PlanOnly) {
        Write-Host "$scriptName`: -PlanOnly - nothing will run."
        Write-Host ("  command : {0} backup-disk --disk {1} --dest `"{2}`" --name {3} --compress zstd --level 5" -f $CliPath, $sysDisk, $DestDir, $name)
        Write-Host ("  image   : {0}" -f $dest)
        Write-Host ("  verify  : {0} verify ""{1}"" --depth full" -f $CliPath, $dest)
        Write-Host ("  after verify OK, {0} item(s) would be deleted:" -f $othersBefore.Count)
        foreach ($item in $othersBefore) {
            $size = if ($item.PSIsContainer) { '<dir>' } else { '{0:N2} GiB' -f ($item.Length / 1GB) }
            Write-Host ("    delete: {0}  [{1}]" -f $item.FullName, $size)
        }
        exit 0
    }

    # ---- backup (ALWAYS full) with live ##PROGRESS streaming ----------------
    Write-Host ("{0}: FULL imaging disk {1} -> {2}" -f $scriptName, $sysDisk, $dest) -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $doneImage = $null
    $logDir = Join-Path $DestDir 'logs'
    if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = $CliPath
    $pinfo.Arguments = "backup-disk --disk $sysDisk --dest `"$DestDir`" --name $name --compress zstd --level 5 --log-dir `"$logDir`""
    $pinfo.UseShellExecute = $false
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $proc = [Diagnostics.Process]::Start($pinfo)
    $errTask = $proc.StandardError.ReadToEndAsync()

    while ($null -ne ($line = $proc.StandardOutput.ReadLine())) {
        if ($line.StartsWith('##PROGRESS ')) {
            try { $j = $line.Substring(11) | ConvertFrom-Json } catch { continue }
            $pct = if ($j.bytesTotal -gt 0) { [math]::Round(100.0 * $j.bytesDone / $j.bytesTotal, 1) } else { 0 }
            $doneGiB = [math]::Round($j.bytesDone / 1GB, 2)
            $totGiB = [math]::Round($j.bytesTotal / 1GB, 2)
            $mbs = [math]::Round($j.speedBytesPerSec / 1MB, 0)
            $eta = if ($j.speedBytesPerSec -gt 0) {
                $s = [math]::Round(($j.bytesTotal - $j.bytesDone) / $j.speedBytesPerSec)
                '{0:00}:{1:00}:{2:00}' -f [int]($s / 3600), [int](($s % 3600) / 60), ($s % 60)
            } else { '--:--:--' }
            $el = '{0:00}:{1:00}:{2:00}' -f [int]($sw.Elapsed.TotalSeconds / 3600), [int](($sw.Elapsed.TotalSeconds % 3600) / 60), [int]($sw.Elapsed.TotalSeconds % 60)
            Write-Host ("`r  {0,8:N2} / {1,8:N2} GiB  {2,5}%  {3,6} MB/s  elapsed {4}  eta {5}   " -f $doneGiB, $totGiB, $pct, $mbs, $el, $eta) -NoNewline
        }
        elseif ($line.StartsWith('##DONE ')) {
            try { $doneImage = ($line.Substring(7) | ConvertFrom-Json).image } catch { }
        }
    }
    $proc.WaitForExit()
    $exitCode = $proc.ExitCode
    Write-Host ''
    if ($exitCode -ne 0) {
        Write-Host ("{0}: backup-disk FAILED (exit {1}): {2}" -f $scriptName, $exitCode, $errTask.Result) -ForegroundColor Red
        if ($doneImage -and (Test-Path -LiteralPath $doneImage)) { Remove-Item -LiteralPath $doneImage -Force }
        elseif (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }
        exit 2
    }

    # resolve the ACTUAL new image (the CLI appends its own timestamp)
    $realImage = if ($doneImage -and (Test-Path -LiteralPath $doneImage)) { $doneImage } else {
        (Get-ChildItem -LiteralPath $DestDir -Filter '*.orf' -File |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    }
    $dest = $realImage

    # ---- MANDATORY full verify BEFORE any deletion --------------------------
    Write-Host "$scriptName`: backup done. Running MANDATORY full verify..." -ForegroundColor Cyan
    $vsw = [Diagnostics.Stopwatch]::StartNew()
    & $CliPath verify "$dest" --depth full --log-dir "$logDir" 2>&1 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "$scriptName`: VERIFY FAILED - keeping old backups untouched, nothing deleted." -ForegroundColor Red
        exit 3
    }
    Write-Host ("{0}: verify OK in {1:N0}s." -f $scriptName, $vsw.Elapsed.TotalSeconds) -ForegroundColor Green

    # ---- cleanup: delete everything in DestDir except the new image ---------
    $image = Get-Item -LiteralPath $dest
    Write-Host ("{0}: image {1:N2} GiB, total {2:N0}s ({3})" -f $scriptName, ($image.Length / 1GB), $sw.Elapsed.TotalSeconds, (Split-Path $dest -Leaf)) -ForegroundColor Green
    $imagePath = (Get-Item -LiteralPath $dest).FullName
    Write-Host ("{0}: cleaning {1} (keeping only the verified image)" -f $scriptName, $destDirRoot) -ForegroundColor Cyan

    foreach ($item in @(Get-ChildItem -LiteralPath $destDirRoot -Force -ErrorAction SilentlyContinue)) {
        $full = $item.FullName
        if ($full -eq $imagePath) {
            Write-Host ("  keep (newest image, never touched): {0}" -f $full)
            continue
        }
        # refuse anything matching the newest image path (string compare, any casing)
        if ($full -ieq $imagePath) { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Write-Host ("  keep (reparse point, never touched): {0}" -f $full)
            continue
        }
        Write-Host ("  deleting: {0}" -f $full)
        if ($item.PSIsContainer) {
            Remove-Item -LiteralPath $full -Recurse -Force
        } else {
            Remove-Item -LiteralPath $full -Force
        }
    }

    # ---- final state check --------------------------------------------------
    $remaining = @(Get-ChildItem -LiteralPath $destDirRoot -Force)
    Write-Host ("{0}: final contents of {1}:" -f $scriptName, $destDirRoot)
    foreach ($r in $remaining) { Write-Host ("  {0}" -f $r.Name) }
    if ($remaining.Count -eq 1 -and $remaining[0].FullName -ieq $imagePath) {
        Write-Host "$scriptName`: SUCCESS - exactly one verified backup remains." -ForegroundColor Green
        exit 0
    } else {
        Write-Host "$scriptName`: WARNING - unexpected leftover items in destination." -ForegroundColor Yellow
        exit 4
    }
}
finally {
    $mutex.ReleaseMutex() | Out-Null
    $mutex.Dispose()
}

# orf-backup.ps1 - OpenReflect system-disk backup (full + incremental cadence)
# Images the disk that hosts C: into F:\win11backup with intelligent sectors,
# VSS consistency and zstd, then runs a mandatory full verify of the new image.
#
# Cadence:
#   - no image yet, dirty newest image, or >= 7 incrementals since the last full
#       -> FULL backup; on success every other .orf chain is removed (single chain kept)
#   - otherwise -> INCREMENTAL of the changed sectors, chained onto the newest image
# Restore (orf-restore.ps1) merges the chain automatically: point it at the newest .orf.
#
# usage:
#   orf-backup.ps1              real backup of the C: disk into F:\win11backup
#   orf-backup.ps1 -PlanOnly    show what would happen, run nothing
#
# NEVER touches F:\win11recovery (the Macrium folder) - this script only ever
# writes F:\win11backup\<name>.orf.

param(
    [switch]$PlanOnly,
    [string]$DestDir = 'F:\win11backup',
    [string]$CliPath = 'F:\backup\windowsapps\installed\OpenReflect\cli\OpenReflect.CLI.exe',
    [int]$MaxIncrementals = 7
)

$ErrorActionPreference = 'Stop'

# ---- admin check -----------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'orf-backup: administrator rights are required (VSS + raw disk read).' -ForegroundColor Red
    exit 2
}

# ---- single-writer mutex ---------------------------------------------------
$mutex = New-Object System.Threading.Mutex($false, 'Global\OpenReflectBackupWriter')
$got = $false
try { $got = $mutex.WaitOne(0) } catch { $got = $false }
if (-not $got) {
    Write-Host 'orf-backup: another backup is already running (Global\OpenReflectBackupWriter held).' -ForegroundColor Red
    exit 2
}

try {
    if (-not (Test-Path $CliPath)) { Write-Host "orf-backup: CLI not found at $CliPath" -ForegroundColor Red; exit 2 }
    if (-not (Test-Path $DestDir)) { New-Item -ItemType Directory -Path $DestDir | Out-Null }

    # ---- source = the disk that hosts C: (system disk, exactly like rmback) --
    $sysDisk = (Get-Partition -DriveLetter $env:SystemDrive.TrimEnd(':')).DiskNumber
    $diskInfo = Get-Disk -Number $sysDisk
    Write-Host ("orf-backup: system disk = disk {0} ({1}, {2:N1} GiB)" -f $sysDisk, $diskInfo.FriendlyName, ($diskInfo.Size / 1GB))

    # ---- chain state: newest image decides full vs incremental ---------------
    $images = @(Get-ChildItem -LiteralPath $DestDir -Filter '*.orf' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    $newest = if ($images.Count -gt 0) { $images[0] } else { $null }
    $parent = $null
    if ($null -ne $newest) {
        $ins = & $CliPath inspect "$($newest.FullName)" 2>$null | ConvertFrom-Json
        if ($ins.complete) {
            $incsSinceFull = 0
            foreach ($img in $images) {
                $j = & $CliPath inspect "$($img.FullName)" 2>$null | ConvertFrom-Json
                if (-not $j.complete) { break }
                if ($j.chainKind -ne 'Incremental') { break }
                $incsSinceFull++
            }
            if ($incsSinceFull -lt $MaxIncrementals) { $parent = $newest.FullName }
            Write-Host ("orf-backup: newest = {0} ({1}, complete); incrementals since full = {2}" -f `
                $newest.Name, $ins.chainKind, $incsSinceFull)
        } else {
            Write-Host 'orf-backup: newest image is INCOMPLETE (dirty shutdown) - taking a new full.' -ForegroundColor Yellow
        }
    }

    # ---- auto name, collision refusal ---------------------------------------
    $rand = -join ((48..57) + (97..122) | Get-Random -Count 6 | ForEach-Object { [char]$_ })
    $kindTag = if ($null -eq $parent) { 'full' } else { 'inc' }
    $name = 'backup_{0}_{1}_{2}' -f $kindTag, (Get-Date -Format 'yyyyMMdd-HHmmss'), $rand
    $dest = Join-Path $DestDir "$name.orf"
    if (Test-Path $dest) { Write-Host "orf-backup: name collision on $dest - refusing." -ForegroundColor Red; exit 2 }

    # ---- cleanup snapshot: on FULL success, every other chain is removed -----
    $othersBefore = @($images | Where-Object { $_.FullName -ne $dest } | Select-Object FullName, Length, LastWriteTime)

    $parentArg = if ($null -ne $parent) { " --parent `"$parent`"" } else { '' }
    if ($PlanOnly) {
        Write-Host 'orf-backup: -PlanOnly - nothing will run.'
        Write-Host ("  command : {0} backup-disk --disk {1} --dest {2} --name {3} --compress zstd --level 5{4}" -f `
            $CliPath, $sysDisk, $DestDir, $name, $parentArg)
        Write-Host ("  image   : {0}" -f $dest)
        Write-Host ("  verify  : {0} verify ""{1}"" --depth full" -f $CliPath, $dest)
        if ($null -eq $parent) {
            Write-Host ("  on success, {0} older image(s) would be removed" -f $othersBefore.Count)
        } else {
            Write-Host '  on success, incremental appended to the chain (nothing removed).'
        }
        exit 0
    }

    # ---- backup with live progress -------------------------------------------
    $modeText = if ($null -eq $parent) { 'FULL' } else { 'INCREMENTAL of ' + (Split-Path $parent -Leaf) }
    Write-Host ("orf-backup: imaging disk {0} -> {1}  [{2}]" -f $sysDisk, $dest, $modeText) -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $lastLine = $null
    $doneImage = $null
    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = $CliPath
    $logDir = Join-Path $DestDir 'logs'
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
    $pinfo.Arguments = "backup-disk --disk $sysDisk --dest `"$DestDir`" --name $name --compress zstd --level 5 --log-dir `"$logDir`"$parentArg"
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
            $lastLine = ('  {0,7:N2} / {1:N2} GiB  {2,5}%  {3,6} MB/s  elapsed {4}  eta {5}' -f $doneGiB, $totGiB, $pct, $mbs, $el, $eta)
            Write-Host ("`r{0}   " -f $lastLine) -NoNewline
        }
        elseif ($line.StartsWith('##DONE ')) {
            $lastLine = $line
            try { $doneImage = ($line.Substring(7) | ConvertFrom-Json).image } catch { }
        }
    }
    $proc.WaitForExit()
    $exitCode = $proc.ExitCode
    Write-Host ''
    if ($exitCode -ne 0) {
        Write-Host ("orf-backup: backup-disk FAILED (exit {0}): {1}" -f $exitCode, $errTask.Result) -ForegroundColor Red
        if ($doneImage -and (Test-Path $doneImage)) { Remove-Item -LiteralPath $doneImage -Force }
        elseif (Test-Path $dest) { Remove-Item -LiteralPath $dest -Force }
        exit 2
    }

    # ---- mandatory full verify ------------------------------------------------
    Write-Host 'orf-backup: backup done. Running MANDATORY full verify...' -ForegroundColor Cyan
    # the CLI appends its own timestamp to the name - resolve the ACTUAL new image
    $realImage = if ($doneImage -and (Test-Path $doneImage)) { $doneImage } else {
        (Get-ChildItem -LiteralPath $DestDir -Filter '*.orf' -File |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    }
    $dest = $realImage
    $vsw = [Diagnostics.Stopwatch]::StartNew()
    & $CliPath verify "$dest" --depth full --log-dir "$DestDir\logs" 2>&1 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'orf-backup: VERIFY FAILED - deleting the unverified image, chain untouched.' -ForegroundColor Red
        Remove-Item -LiteralPath $dest -Force
        exit 3
    }
    Write-Host ("orf-backup: verify OK in {0:N0}s." -f $vsw.Elapsed.TotalSeconds) -ForegroundColor Green

    $image = Get-Item -LiteralPath $dest
    Write-Host ("orf-backup: image {0:N2} GiB, total {1:N0}s ({2})" -f ($image.Length / 1GB), $sw.Elapsed.TotalSeconds, $name) -ForegroundColor Green

    # ---- success cleanup: a FULL restarts the chain (only the new chain stays)
    if ($null -eq $parent) {
        foreach ($old in $othersBefore) {
            if (-not (Test-Path -LiteralPath $old.FullName)) { continue }   # unchanged-since-start check
            $cur = Get-Item -LiteralPath $old.FullName -ErrorAction SilentlyContinue
            if ($null -eq $cur -or $cur.Length -ne $old.Length -or $cur.LastWriteTime -ne $old.LastWriteTime) {
                Write-Host ("  keep (changed since start): {0}" -f $old.FullName); continue
            }
            if ($cur.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                Write-Host ("  keep (reparse point, never touched): {0}" -f $old.FullName); continue
            }
            Write-Host ("  removing old chain image: {0}" -f $old.FullName)
            Remove-Item -LiteralPath $old.FullName -Force
        }
    } else {
        Write-Host 'orf-backup: incremental appended to the chain (nothing removed).'
    }
    Write-Host 'orf-backup: SUCCESS.' -ForegroundColor Green
    exit 0
}
finally {
    $mutex.ReleaseMutex() | Out-Null
    $mutex.Dispose()
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- elevation check (DISM mount + mountvol need admin) ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Output 'NOT-ADMIN'; exit 10 }
Write-Output 'admin: yes'

$vol      = '\\?\Volume{a0dc0353-4398-4ad7-be1c-6e4245f68af6}\'
$letter   = 'X:'
$srcDir   = 'X:\Recovery\WindowsRE'
$envDir   = 'C:\OpenReflectRestoreEnv'
$cliDir   = 'F:\backup\windowsapps\installed\OpenReflect\cli'
$mount    = 'C:\orf-wimmount'
$log      = 'C:\OpenReflectRestoreEnv\build-log.txt'

try {
    # --- 1. temp mount recovery partition (undo: mountvol X: /D) ---
    $existing = (Get-Volume -DriveLetter X -ErrorAction SilentlyContinue)
    if ($existing) { Write-Output 'X: already exists - aborting to avoid collision'; exit 11 }
    & "$env:SystemRoot\System32\mountvol.exe" $letter $vol | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "mountvol failed: $LASTEXITCODE" }
    Write-Output 'mounted X:'

    # --- 2. create env dir + copy winre assets ---
    New-Item -ItemType Directory -Path $envDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $srcDir 'Winre.wim') -Destination (Join-Path $envDir 'orwinre.wim') -Force
    Copy-Item -LiteralPath (Join-Path $srcDir 'boot.sdi')  -Destination (Join-Path $envDir 'boot.sdi')  -Force
    Write-Output 'copied orwinre.wim + boot.sdi'

    # --- 3. DISM mount the COPY (never the registered WinRE) ---
    if (Test-Path $mount) {
        # stale empty mount dir from an aborted run is safe to remove; an active DISM mount is not
        $mountedInfo = & dism.exe /Get-MountedWimInfo 2>$null | Out-String
        if ($mountedInfo -notmatch [regex]::Escape($mount) -and -not (Get-ChildItem -LiteralPath $mount -Force)) {
            Remove-Item -LiteralPath $mount -Recurse -Force
            Write-Output 'removed stale empty mount dir'
        } else { throw "mount dir $mount already exists (active mount or not empty)" }
    }
    New-Item -ItemType Directory -Path $mount -Force | Out-Null
    dism.exe /Mount-Wim /WimFile:"$envDir\orwinre.wim" /Index:1 /MountDir:"$mount"
    if ($LASTEXITCODE -ne 0) { throw "dism mount failed: $LASTEXITCODE" }
    Write-Output 'wim mounted'

    # --- 4. inject self-contained CLI folder ---
    $injOpen = Join-Path $mount 'OpenReflect'
    New-Item -ItemType Directory -Path $injOpen -Force | Out-Null
    Copy-Item -Path (Join-Path $cliDir '*') -Destination $injOpen -Recurse -Force
    Write-Output 'CLI injected'

    # --- 5. offline-restore.cmd (per design doc section 5) ---
    # WinPE has NO PowerShell: the task JSON is parsed with pure batch (findstr).
    # EVERY step is logged to %TASKDRV%\win11backup\restore-log.txt so a failure
    # is always diagnosable. The cmd ALWAYS reboots itself (success: after 5 s;
    # failure: after 30 s so the error is readable; no task: after 15 s).
    $cmd = @'
@echo off
setlocal EnableDelayedExpansion
title OpenReflect offline restore
set DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
echo OpenReflect offline restore environment
set TASKDRV=
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
  if not defined TASKDRV if exist %%D:\win11backup\restore-task.json set TASKDRV=%%D
)
if not defined TASKDRV (
  echo No restore-task.json found on any drive - nothing to do.
  color 4F
  echo Rebooting in 15 seconds...
  timeout /t 15 /nobreak >nul
  wpeutil reboot
  exit /b
)
set TASK=%TASKDRV%:\win11backup\restore-task.json
set LOG=%TASKDRV%:\win11backup\restore-log.txt
echo OpenReflect offline restore - %DATE% %TIME% > "%LOG%"
echo Found restore task: %TASK%
echo task: %TASK%>> "%LOG%"
for /f "tokens=2 delims=:, " %%A in ('findstr /C:"\"image\"" "%TASK%"') do set "IMG=%%~A"
for /f "tokens=2 delims=:, " %%A in ('findstr /C:"\"disk\"" "%TASK%"') do set "DISKNO=%%A"
for /f "tokens=2 delims=:, " %%A in ('findstr /C:"\"skipUnwritten\"" "%TASK%"') do set "SKIPUW=%%A"
if not defined IMG (
  echo Could not parse image path from task file.
  echo failed-parse > %TASKDRV%:\win11backup\restore-failed.marker
  echo parse FAILED - IMG empty>> "%LOG%"
  color 4F
  echo RESTORE FAILED - rebooting in 30 seconds...
  timeout /t 30 /nobreak >nul
  wpeutil reboot
  exit /b
)
set "IMG=!IMG:\\=\!"
set EXTRAS=
if /I "%SKIPUW%"=="true" set EXTRAS=--skip-unwritten
echo Image: %IMG%
echo Disk : %DISKNO%
echo image: %IMG%>> "%LOG%"
echo disk: %DISKNO% skip: %SKIPUW%>> "%LOG%"
echo image exists check:>> "%LOG%"
if exist "%IMG%" (echo   image FOUND>> "%LOG%") else (echo   IMAGE NOT FOUND>> "%LOG%")
echo volumes in WinPE (before dismount):>> "%LOG%"
mountvol >> "%LOG%" 2>&1
rem --- dismount every volume except the WinPE ramdisk (X:) and the backup
rem --- drive: a mounted target volume makes the restore refuse (DestinationInUse)
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
  if /I not "%%D:"=="%TASKDRV%" if /I not "%%D:"=="%SYSTEMDRIVE%" if exist %%D:\ (
    echo dismounting %%D>> "%LOG%"
    mountvol %%D: /D >> "%LOG%" 2>&1
  )
)
echo volumes after dismount:>> "%LOG%"
mountvol >> "%LOG%" 2>&1
color 1F
echo === running restore-disk (preflight first; aborts before any write on failure) ===
\OpenReflect\OpenReflect.CLI.exe restore-disk --image "%IMG%" --disk %DISKNO% --task-file "%TASK%" %EXTRAS% --confirm-device-write >> "%LOG%" 2>&1
set RC=%ERRORLEVEL%
echo restore-disk exit code: %RC%>> "%LOG%"
if %RC% EQU 0 (
  echo done > %TASKDRV%:\win11backup\restore-done.marker
  color 2F
  echo RESTORE SUCCEEDED - full log: restore-log.txt - rebooting automatically...
  timeout /t 5 /nobreak >nul
  wpeutil reboot
) else (
  echo failed > %TASKDRV%:\win11backup\restore-failed.marker
  color 4F
  echo RESTORE FAILED with code %RC% - reason in restore-log.txt - rebooting in 60 seconds...
  type "%LOG%" | findstr /C:"error" /C:"Error" /C:"FAIL"
  timeout /t 60 /nobreak >nul
  wpeutil reboot
)
'@
    Set-Content -Path (Join-Path $injOpen 'offline-restore.cmd') -Value $cmd -Encoding Ascii

    # --- 6. winpeshl.ini: launch the cmd on boot ---
    $ini = @'
[LaunchApps]
%SYSTEMDRIVE%\OpenReflect\offline-restore.cmd
'@
    Set-Content -Path (Join-Path $mount 'Windows\System32\winpeshl.ini') -Value $ini -Encoding Ascii
    Write-Output 'offline-restore.cmd + winpeshl.ini injected'

    # --- 7. commit ---
    dism.exe /Unmount-Wim /MountDir:"$mount" /Commit
    if ($LASTEXITCODE -ne 0) { throw "dism commit failed: $LASTEXITCODE" }
    Write-Output 'wim committed'

    # --- 8. validate: read-only remount, list injected files, discard ---
    dism.exe /Mount-Wim /WimFile:"$envDir\orwinre.wim" /Index:1 /MountDir:"$mount" /ReadOnly
    if ($LASTEXITCODE -ne 0) { throw "dism read-only remount failed" }
    Get-ChildItem (Join-Path $mount 'OpenReflect') -File | Select-Object -First 5 Name,Length | Format-Table -AutoSize | Out-String | Write-Output
    Write-Output ('winpeshl.ini in image: ' + (Test-Path (Join-Path $mount 'Windows\System32\winpeshl.ini')))
    Write-Output ('offline-restore.cmd in image: ' + (Test-Path (Join-Path $mount 'OpenReflect\offline-restore.cmd')))
    dism.exe /Unmount-Wim /MountDir:"$mount" /Discard | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dism unmount-discard failed" }
    Write-Output 'validation remount OK, unmounted'

    # --- 9. env-state.json (bootEntryGuid = existing BCD osloader referencing orwinre) ---
    # derive CLI version from the actual binary instead of hardcoding it
    $cliVersion = 'unknown'
    try {
        $vOut = & (Join-Path $cliDir 'OpenReflect.CLI.exe') --version 2>$null
        if ($LASTEXITCODE -eq 0 -and $vOut) { $cliVersion = ($vOut | Select-Object -First 1) -replace '^OpenReflect\s+', '' }
    } catch { Write-Output ('warning: could not query CLI version: ' + $_.Exception.Message) }
    $state = [ordered]@{
        built         = (Get-Date).ToString('s')
        bootEntryGuid = '{53822684-2ccc-4c4f-a79f-a9d3a59c6e99}'
        sourceWinre   = 'harddisk0\partition4\Recovery\WindowsRE\Winre.wim (read-only copy via temp mount X:)'
        winreVersion  = '10.0.26100.9444 (reagentc /info)'
        cliSource     = $cliDir
        cliVersion    = $cliVersion
        files         = @('orwinre.wim','boot.sdi','env-state.json')
        injected      = @('\OpenReflect\ (self-contained CLI)','\OpenReflect\offline-restore.cmd','\Windows\System32\winpeshl.ini')
        bcdModified   = $false
        notes         = 'BCD osloader already existed; only files rebuilt here. Never rebooted during build. Arming = orf-restore.ps1 (bcdedit /bootsequence one-shot).'
    }
    $state | ConvertTo-Json | Set-Content -Path (Join-Path $envDir 'env-state.json') -Encoding Ascii
    Write-Output 'env-state.json written'
}
catch {
    Write-Output ("ERROR: " + $_.Exception.Message)
    # best-effort cleanup so nothing is left half-mounted
    $mountedInfo = & dism.exe /Get-MountedWimInfo 2>$null | Out-String
    if ($mountedInfo -match [regex]::Escape($mount)) {
        & dism.exe /Unmount-Wim /MountDir:"$mount" /Discard | Out-Null
    }
    exit 20
}
finally {
    # always release the temp drive letter, even on failure (was leaked before)
    if (Test-Path ($letter + '\')) {
        & "$env:SystemRoot\System32\mountvol.exe" $letter /D | Out-Null
        Write-Output 'unmounted X:'
    }
}
Get-ChildItem $envDir | Select-Object Name,Length | Format-Table -AutoSize | Out-String | Write-Output

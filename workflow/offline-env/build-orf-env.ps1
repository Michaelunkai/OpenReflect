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
    if (Test-Path $mount) { throw "mount dir $mount already exists" }
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
    $cmd = @'
@echo off
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
  echo Press any key to reboot...
  pause >nul
  wpeutil reboot
  exit /b
)
set TASK=%TASKDRV%:\win11backup\restore-task.json
echo Found restore task: %TASK%
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "(Get-Content -Raw '%TASK%' | ConvertFrom-Json).image"`) do set IMAGE=%%I
for /f "usebackq delims=" %%N in (`powershell -NoProfile -Command "(Get-Content -Raw '%TASK%' | ConvertFrom-Json).disk"`) do set DISKNO=%%N
for /f "usebackq delims=" %%S in (`powershell -NoProfile -Command "(Get-Content -Raw '%TASK%' | ConvertFrom-Json).skipUnwritten"`) do set SKIPUW=%%S
echo Image: %IMAGE%
echo Disk : %DISKNO%
set EXTRAS=
if /I "%SKIPUW%"=="True" set EXTRAS=--skip-unwritten
color 1F
echo === running restore-disk (preflight first; aborts before any write on failure) ===
\OpenReflect\OpenReflect.CLI.exe restore-disk --image "%IMAGE%" --disk %DISKNO% --task-file "%TASK%" %EXTRAS% --confirm-device-write
if %ERRORLEVEL% EQU 0 (
  echo done > %TASKDRV%:\win11backup\restore-done.marker
  color 2F
  echo RESTORE SUCCEEDED.
) else (
  echo failed > %TASKDRV%:\win11backup\restore-failed.marker
  color 4F
  echo RESTORE FAILED with code %ERRORLEVEL%.
)
echo Press any key to reboot...
pause >nul
wpeutil reboot
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
    $state = [ordered]@{
        built         = (Get-Date).ToString('s')
        bootEntryGuid = '{53822684-2ccc-4c4f-a79f-a9d3a59c6e99}'
        sourceWinre   = 'harddisk0\partition4\Recovery\WindowsRE\Winre.wim (read-only copy via temp mount X:)'
        winreVersion  = '10.0.26100.9444 (reagentc /info)'
        cliSource     = $cliDir
        cliVersion    = '0.2.0+38a4160'
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
    & dism.exe /Get-MountedWimInfo 2>$null | Select-String -Pattern $mount -Quiet | ForEach-Object {
        & dism.exe /Unmount-Wim /MountDir:"$mount" /Discard | Out-Null
    }
    exit 20
}
finally {
    if (Test-Path (Join-Path $envDir 'x.tmp')) { }
}
# --- 10. release temp drive letter ---
& "$env:SystemRoot\System32\mountvol.exe" $letter /D | Out-Null
Write-Output 'unmounted X:'
Get-ChildItem $envDir | Select-Object Name,Length | Format-Table -AutoSize | Out-String | Write-Output

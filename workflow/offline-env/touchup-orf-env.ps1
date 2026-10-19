Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Output 'NOT-ADMIN'; exit 10 }

$envDir = 'C:\OpenReflectRestoreEnv'
$wim    = Join-Path $envDir 'orwinre.wim'
$mount  = 'C:\orf-wimmount'
$cliDir = 'F:\backup\windowsapps\installed\OpenReflect\cli'
$cmdBak = 'F:\Downloads\offline-restore.cmd.keep'

# --- 1. mount for read-write ---
if (Test-Path $mount) { throw 'mount dir left over' }
New-Item -ItemType Directory -Path $mount -Force | Out-Null
dism.exe /English /Mount-Wim /WimFile:"$wim" /Index:1 /MountDir:"$mount"
if ($LASTEXITCODE -ne 0) { throw 'mount failed' }

try {
    # --- 2. preserve offline-restore.cmd, replace \OpenReflect\ contents ---
    Copy-Item "$mount\OpenReflect\offline-restore.cmd" $cmdBak -Force
    Remove-Item "$mount\OpenReflect" -Recurse -Force
    New-Item -ItemType Directory -Path "$mount\OpenReflect" -Force | Out-Null
    Copy-Item -Path (Join-Path $cliDir '*') -Destination "$mount\OpenReflect" -Recurse -Force
    Copy-Item $cmdBak "$mount\OpenReflect\offline-restore.cmd" -Force
    # --- 3. confirm winpeshl.ini intact ---
    $ini = Get-Content "$mount\Windows\System32\winpeshl.ini" -Raw
    Write-Output ('winpeshl.ini intact: ' + ($ini -match 'offline-restore\.cmd'))
    Write-Output ('injected file count: ' + (Get-ChildItem "$mount\OpenReflect" -Recurse -File).Count)
    # --- 4. commit + unmount ---
    dism.exe /English /Unmount-Wim /MountDir:"$mount" /Commit
    if ($LASTEXITCODE -ne 0) { throw 'commit failed' }
    Write-Output 'committed'
}
catch {
    Write-Output ('ERROR: ' + $_.Exception.Message)
    dism.exe /English /Unmount-Wim /MountDir:"$mount" /Discard | Out-Null
    exit 20
}
Remove-Item $mount -Force -ErrorAction SilentlyContinue

# --- 5. read-only verification remount ---
dism.exe /English /Mount-Wim /WimFile:"$wim" /Index:1 /MountDir:"$mount" /ReadOnly
if ($LASTEXITCODE -ne 0) { throw 'verify mount failed' }
Write-Output ('CLI exe present            : ' + (Test-Path "$mount\OpenReflect\OpenReflect.CLI.exe"))
Write-Output ('offline-restore.cmd present: ' + (Test-Path "$mount\OpenReflect\offline-restore.cmd"))
Write-Output ('winpeshl.ini present       : ' + (Test-Path "$mount\Windows\System32\winpeshl.ini"))
Write-Output ('file count in \OpenReflect\: ' + (Get-ChildItem "$mount\OpenReflect" -Recurse -File).Count)
Write-Output ('size MB: ' + [math]::Round(((Get-ChildItem "$mount\OpenReflect" -Recurse -File | Measure-Object Length -Sum).Sum)/1MB,1))
dism.exe /English /Unmount-Wim /MountDir:"$mount" /Discard
Write-Output ('verify unmount exit: ' + $LASTEXITCODE)
Remove-Item $mount -Force -ErrorAction SilentlyContinue

# --- 6. update env-state.json ---
$statePath = Join-Path $envDir 'env-state.json'
$state = Get-Content $statePath -Raw | ConvertFrom-Json
$state.built      = (Get-Date).ToString('s')
$state.cliVersion = '0.2.0+ff3c769 (MftFileLocator hang fix)'
$state.notes = $state.notes + ' Re-injected CLI from stable deploy (ff3c769) on ' + (Get-Date).ToString('s') + '; BCD still untouched.'
$state | ConvertTo-Json | Set-Content $statePath -Encoding Ascii
Write-Output '--- env-state.json ---'
Get-Content $statePath

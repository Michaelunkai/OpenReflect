Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$envDir = 'C:\OpenReflectRestoreEnv'
$mount  = 'C:\orf-wimmount'
if (-not (Test-Path $mount)) { New-Item -ItemType Directory -Path $mount -Force | Out-Null }
try {
    dism.exe /English /Mount-Wim /WimFile:"$envDir\orwinre.wim" /Index:1 /MountDir:"$mount" /ReadOnly | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'read-only mount failed' }
    Write-Output ('OpenReflect dir file count : ' + (Get-ChildItem "$mount\OpenReflect" -Recurse -File).Count)
    Write-Output ('OpenReflect dir size MB    : ' + [math]::Round(((Get-ChildItem "$mount\OpenReflect" -Recurse -File | Measure-Object Length -Sum).Sum)/1MB,1))
    Write-Output ('CLI exe present            : ' + (Test-Path "$mount\OpenReflect\OpenReflect.CLI.exe"))
    Write-Output ('offline-restore.cmd present: ' + (Test-Path "$mount\OpenReflect\offline-restore.cmd"))
    Write-Output ('winpeshl.ini present       : ' + (Test-Path "$mount\Windows\System32\winpeshl.ini"))
    Write-Output '--- winpeshl.ini content ---'
    Get-Content "$mount\Windows\System32\winpeshl.ini"
    Write-Output '--- offline-restore.cmd (first 6 lines) ---'
    Get-Content "$mount\OpenReflect\offline-restore.cmd" | Select-Object -First 6
}
finally {
    dism.exe /English /Unmount-Wim /MountDir:"$mount" /Discard | Out-Null
    Write-Output ('unmount exit: ' + $LASTEXITCODE)
    Remove-Item $mount -Force -ErrorAction SilentlyContinue
}
Write-Output '--- final env dir ---'
Get-ChildItem $envDir | Select-Object Name,Length | Format-Table -AutoSize | Out-String

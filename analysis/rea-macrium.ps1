$ErrorActionPreference = 'Continue'
$rea = 'F:\backup\windowsapps\installed\rea\scripts\rea.mjs'
$reflect = 'C:\Program Files\Macrium\Reflect'

Write-Host '=== S07: REA health ==='
node $rea --version
node $rea --help 2>&1 | Select-Object -First 6

Write-Host '=== S08: artifact inventory ==='
$bin = Get-ChildItem $reflect -Include *.exe,*.dll,*.sys -Recurse -ErrorAction SilentlyContinue
$common = Get-ChildItem 'C:\Program Files\Macrium\Common' -Include *.exe,*.dll,*.sys -Recurse -ErrorAction SilentlyContinue
$all = @($bin) + @($common) | Where-Object { $_ -and $_.Length -gt 0 }
$all | Sort-Object Length -Descending | Select-Object -First 25 @{n='KB';e={[math]::Round($_.Length/1KB)}}, @{n='Ver';e={$_.VersionInfo.FileVersion}}, Name, FullName | Format-Table -AutoSize | Out-String -Width 220
Write-Host ("total binaries: " + $all.Count)

Write-Host '=== S10: Macrium services/drivers ==='
Get-Service | Where-Object { $_.Name -match 'macrium|reflect' } | Format-Table Name, Status, DisplayName -AutoSize
Get-CimInstance Win32_SystemDriver | Where-Object { $_.Name -match 'macrium|reflect' } | Format-Table Name, State, PathName -AutoSize | Out-String -Width 220

Write-Host '=== S09: REA binary overview of main exe ==='
$mainExe = ($all | Where-Object Name -eq 'Reflect.exe' | Select-Object -First 1).FullName
if (-not $mainExe) { $mainExe = ($all | Sort-Object Length -Descending | Select-Object -First 1).FullName }
Write-Host "target: $mainExe"
node $rea binary_overview 2>&1 | Select-Object -First 12

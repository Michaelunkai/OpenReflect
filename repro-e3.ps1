# Standalone E3 backup-stage repro: VHD -> intelligent backup via CLI, console visible
$ErrorActionPreference = 'Stop'
$dir = "C:\Temp\e3repro"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory -Path $dir | Out-Null
$vhdx = "$dir\src.vhdx"
$before = (Get-Disk | ForEach-Object Number)
@"
create vdisk file="$vhdx" maximum=512 type=expandable
select vdisk file="$vhdx"
attach vdisk
convert gpt
create partition primary
format fs=ntfs quick
assign letter=R
"@ | Set-Content "$dir\dp1.txt" -Encoding Ascii
diskpart /s "$dir\dp1.txt" | Out-Host
$disk = (Get-Disk | Where-Object { $_.Number -notin $before -and $_.FriendlyName -eq 'Msft Virtual Disk' }).Number
Write-Host "REPRO: attached as disk $disk"
Set-Content R:\hello.txt -Value "repro payload line"
fsutil sparse setflag R:\hello.txt | Out-Null
$cli = "F:\study\Platforms\Windows\Applications\Desktop\Utilities\System\Backup\Imaging\OpenReflect\src\CLI\bin\Debug\net10.0\OpenReflect.CLI.exe"
Write-Host "REPRO: starting backup..."
$sw = [Diagnostics.Stopwatch]::StartNew()
& $cli backup-disk --disk $disk --dest $dir --name repro --compress zstd --level 3 2>&1 | ForEach-Object { Write-Host ("[{0,7:N1}s] {1}" -f $sw.Elapsed.TotalSeconds, $_) }
Write-Host "REPRO: exit=$LASTEXITCODE elapsed=$([math]::Round($sw.Elapsed.TotalSeconds,1))s"
# cleanup
@"
select vdisk file="$vhdx"
detach vdisk
"@ | Set-Content "$dir\dp2.txt" -Encoding Ascii
diskpart /s "$dir\dp2.txt" | Out-Null
Write-Host "REPRO: detached. DONE"

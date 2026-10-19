Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
$raw = (& "$env:SystemRoot\System32\mountvol.exe") -join "`n"
$guids = [regex]::Matches($raw, '\\\\\?\\Volume\{[a-f0-9-]+\}\\') | ForEach-Object { $_.Value } | Select-Object -Unique
Write-Output ("volumes found: " + $guids.Count)
foreach ($g in $guids) {
    $base = $g.TrimEnd('\')
    $probe = $base + '\Recovery\WindowsRE\Winre.wim'
    if (Test-Path -LiteralPath $probe) {
        $sdi = $base + '\Recovery\WindowsRE\boot.sdi'
        $w = Get-Item -LiteralPath $probe
        Write-Output ("HIT " + $g)
        Write-Output ("  Winre.wim " + $w.Length + " bytes  " + $w.LastWriteTime)
        if (Test-Path -LiteralPath $sdi) { Write-Output ("  boot.sdi  " + (Get-Item -LiteralPath $sdi).Length + " bytes") }
        else { Write-Output "  boot.sdi  MISSING" }
    }
}
Write-Output "probe done"

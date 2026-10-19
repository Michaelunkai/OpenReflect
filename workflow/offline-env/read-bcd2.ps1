Set-StrictMode -Version Latest
$root = 'HKLM:\BCD00000000\Objects'
$keys = Get-ChildItem $root
Write-Output ("total objects: " + $keys.Count)
foreach ($k in $keys) {
    $id = $k.PSChildName
    $desc = Get-ItemProperty ("$root\$id\Description") -ErrorAction SilentlyContinue
    if (-not $desc) { continue }
    if ($desc.Type -ne 0x10200003) { continue }
    $pathEl = Get-ItemProperty ("$root\$id\Elements\22000002") -ErrorAction SilentlyContinue
    $path = if ($pathEl) { $pathEl.Element } else { '' }
    $hit = $false
    $elems = Get-ChildItem ("$root\$id\Elements") -ErrorAction SilentlyContinue
    foreach ($e in $elems) {
        $v = (Get-ItemProperty $e.PSPath -ErrorAction SilentlyContinue).Element
        if ($v -is [byte[]] -and ([Text.Encoding]::Unicode.GetString($v)) -match 'orwinre') { $hit = $true }
        if ($v -is [string] -and $v -match 'orwinre') { $hit = $true }
    }
    Write-Output ("OSLOADER " + $id + " path=" + $path + " orwinre-ref=" + $hit)
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# READ-ONLY enumeration of the system BCD hive (no bcdedit, no writes)
$root = 'HKLM:\BCD00000000\Objects'
$keys = Get-ChildItem $root
Write-Output ("total objects: " + $keys.Count)
foreach ($k in $keys) {
    $id = $k.PSChildName
    $desc = Get-ItemProperty ("$root\$id\Description") -ErrorAction SilentlyContinue
    if ($desc -and $desc.Type -eq 0x10200003) {  # osloader
        $el = Get-ItemProperty ("$root\$id\Elements") -ErrorAction SilentlyContinue
        $vals = @{}
        $sub = Get-ChildItem ("$root\$id\Elements") -ErrorAction SilentlyContinue
        foreach ($e in $sub) {
            $p = Get-ItemProperty $e.PSPath -ErrorAction SilentlyContinue
            $vals[$e.PSChildName] = $p.'(default)'
        }
        $path = $vals['22000002']; $dev = $vals['11000001']; $osdev = $vals['21000001']
        $line = "OSLOADER $id  path=$path"
        if ($dev -is [byte[]]) { $line += " device-ramdisk-ish(bytes " + $dev.Length + ")" }
        Write-Output $line
        if ($path -match 'winload' -and ($dev -is [byte[]] -or $osdev -is [byte[]])) {
            # decode element 11000001 (device) loosely: look for 'orwinre' string in bytes
            $asText = if ($dev -is [byte[]]) { [Text.Encoding]::Unicode.GetString($dev) } else { '' }
            $asText2 = if ($osdev -is [byte[]]) { [Text.Encoding]::Unicode.GetString($osdev) } else { '' }
            if ($asText + $asText2 -match 'orwinre') { Write-Output "  >>> MATCHES OpenReflectRestoreEnv (orwinre.wim) <<<" }
        }
    }
}

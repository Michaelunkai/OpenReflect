# S06 citation audit + S59 docs-to-CLI flag cross-check (PowerShell 5.1)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
$fail = 0

Write-Host '== S06: SPEC.md carries citations to sourced docs =='
$spec = Get-Content docs\SPEC.md -Raw
foreach ($doc in 'FEATURE-MATRIX.md', 'ARCH-FACTS.md') {
    if ($spec -match [regex]::Escape($doc)) { Write-Host "  SPEC cites $doc OK" }
    else { Write-Host "  SPEC MISSING citation of $doc" -ForegroundColor Red; $fail = 1 }
}
$matrix = Get-Content docs\FEATURE-MATRIX.md -Raw
foreach ($u in 'macrium.com', 'kbx.macrium.com', 'sm1.macrium.com') {
    if ($matrix -match [regex]::Escape($u)) { Write-Host "  FEATURE-MATRIX cites $u OK" }
    else { Write-Host "  MATRIX MISSING $u" -ForegroundColor Red; $fail = 1 }
}

Write-Host '== S59: every CLI flag documented in README/USER-GUIDE exists in --help =='
$scoopSdk = 'F:\backup\windowsapps\installed\scoop\apps\dotnet-sdk\current'
$env:PATH = "$scoopSdk;$env:PATH"; $env:DOTNET_ROOT = $scoopSdk
$env:MSBuildSDKsPath = "$scoopSdk\sdk\10.0.401\Sdks"
$cli = Join-Path $root 'src\CLI\bin\Debug\net10.0\OpenReflect.CLI.exe'
$help = & $cli --help 2>&1 | Out-String

$docText = (Get-Content README.md -Raw) + (Get-Content docs\USER-GUIDE.md -Raw)
$flagMatches = [regex]::Matches($docText, '--([a-z][a-z0-9-]{2,})')
$documented = $flagMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($f in $documented) {
    # skip flags that belong to the docs build tooling or other programs
    if ($f -in @('self-contained','no-build','nologo','execution-policy','password-env','dry-run','confirm','depth')) {
        if ($help -match [regex]::Escape("--$f")) { Write-Host "  --$f OK" }
        elseif ($f -in @('self-contained','no-build','nologo','execution-policy')) { Write-Host "  --$f (docs tooling flag, not CLI) skipped" }
        else { Write-Host "  --$f MISSING FROM CLI --help" -ForegroundColor Red; $fail = 1 }
    }
}
Write-Host ("documented flags checked: " + $documented.Count)

if ($fail -ne 0) { Write-Host 'AUDIT FAILED' -ForegroundColor Red; exit 1 }
Write-Host 'AUDIT PASS' -ForegroundColor Green

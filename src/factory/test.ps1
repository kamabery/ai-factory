<#
.SYNOPSIS
  Runs the .NET tests by tier, prints the tier counts, and with -Tier all applies the coverage gates
  (docs/TESTING.md): 90% line coverage on Factory.Core, 70% on every other assembly the tests reach.
.EXAMPLE
  pwsh src/factory/test.ps1                 # unit + integration, counts and gates (what CI's Linux job runs)
  pwsh src/factory/test.ps1 -Tier unit      # unit tests only, no gate (CI's Windows and macOS jobs)
#>
param(
    [ValidateSet('unit', 'integration', 'all')] [string] $Tier = 'all',
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root '../../artifacts/test-results' | ForEach-Object { [IO.Path]::GetFullPath($_) }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$gates = @{ 'Factory.Core' = 0.90 }
$defaultGate = 0.70
$excluded = 'Factory.AppHost', 'Factory.ServiceDefaults'

if (-not $NoBuild) {
    dotnet build (Join-Path $root 'Factory.slnx') -v q -nologo
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

$projects = [ordered]@{ unit = 'Factory.UnitTests'; integration = 'Factory.IntegrationTests' }
$tiers = if ($Tier -eq 'all') { @($projects.Keys) } else { @($Tier) }
$counts = [ordered]@{}
$failed = $false
foreach ($t in $tiers) {
    $dir = Join-Path $out $t
    $log = dotnet test --project (Join-Path $root "$($projects[$t])/$($projects[$t]).csproj") --no-build `
        --coverage --coverage-output-format cobertura --results-directory $dir 2>&1
    $code = $LASTEXITCODE
    $log | Write-Host
    $total = ($log | Select-String '^\s*total:\s*(\d+)' | Select-Object -Last 1).Matches.Groups[1].Value
    $passed = ($log | Select-String '^\s*succeeded:\s*(\d+)' | Select-Object -Last 1).Matches.Groups[1].Value
    $counts[$t] = [pscustomobject]@{ Total = [int]$total; Passed = [int]$passed }
    if ($code) { $failed = $true }
}

# Line coverage per assembly, a line counted once however many test projects run it.
$lines = @{}
foreach ($f in Get-ChildItem $out -Recurse -Filter '*.cobertura.xml') {
    foreach ($pkg in ([xml](Get-Content $f -Raw)).coverage.packages.package) {
        if ($pkg.name -in $excluded -or $pkg.name -like '*Tests') { continue }
        if (-not $lines.ContainsKey($pkg.name)) { $lines[$pkg.name] = @{} }
        foreach ($cls in $pkg.classes.class) {
            foreach ($l in $cls.lines.line) {
                $key = "$($cls.filename):$($l.number)"
                $lines[$pkg.name][$key] = [bool]($lines[$pkg.name][$key]) -or ([int]$l.hits -gt 0)
            }
        }
    }
}

$summary = @('### .NET tests', '', '| Tier | Tests | Passed |', '| --- | --- | --- |')
$all = ($counts.Values | Measure-Object Total -Sum).Sum
foreach ($t in $counts.Keys) {
    $pct = if ($all) { [int](100 * $counts[$t].Total / $all) } else { 0 }
    $summary += "| $t | $($counts[$t].Total) ($pct%) | $($counts[$t].Passed) |"
}
$summary += '', '| Assembly | Line coverage | Gate |', '| --- | --- | --- |'
foreach ($name in $lines.Keys | Sort-Object) {
    $set = $lines[$name]
    $rate = if ($set.Count) { @($set.Values | Where-Object { $_ }).Count / $set.Count } else { 1 }
    $gate = if ($gates.ContainsKey($name)) { $gates[$name] } else { $defaultGate }
    $ok = $Tier -ne 'all' -or $rate -ge $gate
    if (-not $ok) { $failed = $true }
    $mark = if ($Tier -ne 'all') { 'not gated' } elseif ($ok) { "ok (≥ $($gate * 100)%)" } else { "**FAIL** (< $($gate * 100)%)" }
    $summary += ('| {0} | {1:P1} | {2} |' -f $name, $rate, $mark)
}
$summary | Write-Host
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content $env:GITHUB_STEP_SUMMARY }
exit $(if ($failed) { 1 } else { 0 })

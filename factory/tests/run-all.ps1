<#
.SYNOPSIS
  Runs the factory's Pester suites in parallel, one process per test file, plus factory.Tests.ps1
  on the azure state backend (blob stub), and prints the counts and each file's time.
.DESCRIPTION
  The tests set process-wide environment variables (FACTORY_HOME, FACTORY_WORK, ...), so each file
  runs in its own pwsh (Start-Job), never in a shared runspace. Each file already has its own
  TestDrive state and random stub ports. Exits 1 if any test failed or any file did not run.
.EXAMPLE
  pwsh factory/tests/run-all.ps1                    # everything, local + azure
  pwsh factory/tests/run-all.ps1 -NoAzure -Throttle 4
  pwsh factory/tests/run-all.ps1 -ResultDir out     # also NUnit XML per file (CI)
#>
param(
    [int] $Throttle = [Math]::Max(2, [Environment]::ProcessorCount / 2),
    [switch] $NoAzure,
    [string] $ResultDir
)
$ErrorActionPreference = 'Stop'

$files = @(Get-ChildItem $PSScriptRoot -Filter '*.Tests.ps1' | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Path = $_.FullName; State = 'local' } })
if (-not $NoAzure) { $files += [pscustomobject]@{ Name = 'factory.Tests.ps1'; Path = (Join-Path $PSScriptRoot 'factory.Tests.ps1'); State = 'azure' } }
# The slowest first, so the long runs start at once and the short ones fill in around them.
$order = 'runner', 'factory', 'serve', 'canary', 'state-factory', 'idea-turn'
$files = @($files | Sort-Object { $i = [array]::IndexOf($order, ($_.Name -replace '\.Tests\.ps1$', '')); if ($i -lt 0) { 99 } else { $i } }, State)
if ($ResultDir) { New-Item -ItemType Directory -Force $ResultDir | Out-Null; $ResultDir = (Resolve-Path $ResultDir).Path }

$run = {
    param($path, $state, $resultDir)
    if ($state -eq 'azure') { $env:FACTORY_TEST_STATE = 'azure' } else { Remove-Item Env:FACTORY_TEST_STATE -ErrorAction SilentlyContinue }
    $c = New-PesterConfiguration
    $c.Run.Path = $path
    $c.Run.PassThru = $true
    $c.Output.Verbosity = 'None'
    if ($resultDir) {
        $c.TestResult.Enabled = $true
        $c.TestResult.OutputPath = Join-Path $resultDir "$([IO.Path]::GetFileNameWithoutExtension($path))-$state.xml"
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-Pester -Configuration $c
    [pscustomobject]@{
        Passed = $r.PassedCount; Failed = $r.FailedCount; Skipped = $r.SkippedCount; Seconds = [int]$sw.Elapsed.TotalSeconds
        FailedTests = @($r.Failed | ForEach-Object { $_.ExpandedPath })
        RunError = if ($r.Result -eq 'Failed' -and $r.FailedCount -eq 0) { "$($r.FailedBlocksCount) block(s) failed to run" } else { $null }
    }
}

$total = [Diagnostics.Stopwatch]::StartNew()
$pending = [Collections.Generic.Queue[object]]::new([object[]]$files)
$running = @{}
$results = @()
while ($pending.Count -or $running.Count) {
    while ($pending.Count -and $running.Count -lt $Throttle) {
        $f = $pending.Dequeue()
        $job = Start-Job -ScriptBlock $run -ArgumentList $f.Path, $f.State, $ResultDir -WorkingDirectory (Split-Path $PSScriptRoot -Parent | Split-Path -Parent)
        $running[$job.Id] = $f
    }
    $done = Wait-Job -Job @(Get-Job -Id @($running.Keys)) -Any
    $f = $running[$done.Id]; $running.Remove($done.Id)
    $r = Receive-Job $done -ErrorAction SilentlyContinue | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['Passed'] } | Select-Object -Last 1
    Remove-Job $done -Force
    if (-not $r) { $r = [pscustomobject]@{ Passed = 0; Failed = 0; Skipped = 0; Seconds = 0; FailedTests = @(); RunError = 'no result (the job died)' } }
    $results += [pscustomobject]@{ File = $f.Name; State = $f.State; Passed = $r.Passed; Failed = $r.Failed; Skipped = $r.Skipped; Seconds = $r.Seconds; FailedTests = $r.FailedTests; RunError = $r.RunError }
    $mark = if ($r.Failed -or $r.RunError) { 'FAIL' } else { 'ok  ' }
    Write-Host ("{0} {1,-26} {2,-5} {3,4} passed {4,3} failed {5,4}s" -f $mark, $f.Name, $f.State, $r.Passed, $r.Failed, $r.Seconds)
}

Write-Host ''
foreach ($s in 'local', 'azure') {
    $rs = @($results | Where-Object State -eq $s)
    if (-not $rs) { continue }
    Write-Host ("{0}: passed {1} failed {2} skipped {3}" -f $s, ($rs | Measure-Object Passed -Sum).Sum, ($rs | Measure-Object Failed -Sum).Sum, ($rs | Measure-Object Skipped -Sum).Sum)
}
# Minutes may pass 60: a timespan's mm format would wrap them.
$fmt = { param([timespan] $t) '{0}:{1:00}' -f [int][Math]::Floor($t.TotalMinutes), $t.Seconds }
Write-Host ("wall {0}, files summed {1}" -f (& $fmt $total.Elapsed), (& $fmt ([timespan]::FromSeconds(($results | Measure-Object Seconds -Sum).Sum))))
$bad = @($results | Where-Object { $_.Failed -or $_.RunError })
foreach ($b in $bad) {
    if ($b.RunError) { Write-Host "  $($b.File) [$($b.State)]: $($b.RunError)" }
    foreach ($t in $b.FailedTests) { Write-Host "  $($b.File) [$($b.State)]: $t" }
}
exit $(if ($bad) { 1 } else { 0 })

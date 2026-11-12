<#
.SYNOPSIS
  Runs the factory's Pester suites in parallel, one process per test block, plus factory.Tests.ps1
  on the azure state backend (blob stub), and prints the counts and each unit's time.
.DESCRIPTION
  Pester discovery splits each file into units: a block with no child blocks is one unit, a block
  holding only child blocks is split into them, and a block with tests of its own and child blocks
  (deploy) stays whole, since a name filter can't separate its own tests safely. Each unit runs in
  its own pwsh (Start-Job): the tests set process-wide environment variables (FACTORY_HOME, ...), so
  never a shared runspace. Each already has its own TestDrive state and random stub ports.
  Units start longest first, by their time on the last run (kept in %TEMP%). Exits 1 if any test
  failed, a unit did not run, or the units' test count differs from discovery's.
.EXAMPLE
  pwsh factory/tests/run-all.ps1                    # everything, local + azure
  pwsh factory/tests/run-all.ps1 -NoAzure -Throttle 4
  pwsh factory/tests/run-all.ps1 -ResultDir out     # also NUnit XML per unit
  pwsh factory/tests/run-all.ps1 -Shard 2/4         # the second of four shards (split a run across machines)
  pwsh factory/tests/run-all.ps1 -Tag Ported        # the ported commands' blocks, on PowerShell and on .NET
  pwsh factory/tests/run-all.ps1 -Impl dotnet       # everything, with the ported commands on .NET (FACTORY_IMPL)
#>
param(
    # The tests mostly wait on process starts and git, not CPU, so one unit per core.
    [int] $Throttle = [Math]::Max(2, [Environment]::ProcessorCount),
    [switch] $NoAzure,
    [string] $ResultDir,
    # k/n: run only shard k of n. Every shard computes the same assignment (units longest first, each
    # to the shard with the least time so far), so the n shards together run every unit once.
    [string] $Shard,
    # Print the units this run would take, and exit.
    [switch] $List,
    # Only the blocks with this tag, each run twice: FACTORY_IMPL=ps and dotnet (roadmap spec 01).
    [string] $Tag,
    # Without -Tag: the implementation every unit runs (FACTORY_IMPL); the default is PowerShell's.
    [ValidateSet('ps', 'dotnet')] [string] $Impl
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$timesPath = Join-Path ([IO.Path]::GetTempPath()) 'ai-factory-run-all-times.json'
$lastTimes = @{}
try { if (Test-Path $timesPath) { $lastTimes = Get-Content $timesPath -Raw | ConvertFrom-Json -AsHashtable } } catch { }

# The units a block splits into: { Filter (a FullName pattern, or $null for the whole file), Tests }.
function Get-Units($block) {
    if ($block.Blocks.Count -and -not $block.Tests.Count) { foreach ($b in $block.Blocks) { Get-Units $b }; return }
    [pscustomobject]@{ Filter = "$([Management.Automation.WildcardPattern]::Escape($block.ExpandedPath)).*"; Path = $block.ExpandedPath; Tests = [int](Get-NestedCount $block); Tags = @($block.Tag) }
}
function Get-NestedCount($block) { @($block.Tests).Count + (@($block.Blocks | ForEach-Object { Get-NestedCount $_ }) | Measure-Object -Sum).Sum }

$units = @()
$expected = @{ local = 0; azure = 0 }
foreach ($file in Get-ChildItem $PSScriptRoot -Filter '*.Tests.ps1') {
    $c = New-PesterConfiguration
    $c.Run.Path = $file.FullName; $c.Run.SkipRun = $true; $c.Run.PassThru = $true; $c.Output.Verbosity = 'None'
    $found = Invoke-Pester -Configuration $c
    $states = @('local') + $(if (-not $NoAzure -and $file.Name -eq 'factory.Tests.ps1') { 'azure' })
    foreach ($root in $found.Containers[0].Blocks) {
        foreach ($u in Get-Units $root) {
            if ($Tag -and $u.Tags -notcontains $Tag) { continue }
            foreach ($s in $states) {
                foreach ($unitImpl in $(if ($Tag) { 'ps', 'dotnet' } elseif ($Impl) { $Impl } else { '' })) {
                    $label = "$($file.Name) | $($u.Path) | $s" + $(if ($unitImpl) { " | $unitImpl" })
                    $units += [pscustomobject]@{ File = $file.FullName; Name = $file.Name; Filter = $u.Filter; Path = $u.Path; State = $s; Impl = $unitImpl; Tests = $u.Tests; Label = $label
                        Last = if ($lastTimes.ContainsKey($label)) { [int]$lastTimes[$label] } else { $u.Tests } }
                    $expected[$s] += $u.Tests
                }
            }
        }
    }
}
$units = @($units | Sort-Object Last, Label -Descending)
if ($Shard) {
    if ($Shard -notmatch '^(\d+)/(\d+)$' -or [int]$Matches[1] -lt 1 -or [int]$Matches[1] -gt [int]$Matches[2]) { throw "-Shard must be k/n with 1 <= k <= n, not '$Shard'" }
    $k = [int]$Matches[1]; $nShards = [int]$Matches[2]
    $load = [double[]]::new($nShards)
    $mine = foreach ($u in $units) {
        $i = [array]::IndexOf($load, ($load | Measure-Object -Minimum).Minimum)
        $load[$i] += [Math]::Max(1, $u.Last)
        if ($i -eq $k - 1) { $u }
    }
    $units = @($mine)
    $expected = @{ local = 0; azure = 0 }
    foreach ($u in $units) { $expected[$u.State] += $u.Tests }
}
if ($List) { $units | ForEach-Object { '{0,5} {1,4}  {2}' -f $_.Last, $_.Tests, $_.Label }; exit 0 }
if ($Tag -and -not $units) { throw "no test block is tagged '$Tag'" }
# FACTORY_IMPL=dotnet runs the CLI factory.ps1 forwards to; build it once, before the units start.
if ($Tag -or $Impl -eq 'dotnet') {
    foreach ($p in 'Factory.Cli', 'Factory.Runner', 'Factory.Server') {
        dotnet build (Join-Path $repo "src/factory/$p/$p.csproj") -v q -nologo
        if ($LASTEXITCODE) { throw "dotnet build of $p failed" }
    }
}
if ($ResultDir) { New-Item -ItemType Directory -Force $ResultDir | Out-Null; $ResultDir = (Resolve-Path $ResultDir).Path }

$run = {
    param($path, $filter, $state, $resultFile, $impl)
    if ($state -eq 'azure') { $env:FACTORY_TEST_STATE = 'azure' } else { Remove-Item Env:FACTORY_TEST_STATE -ErrorAction SilentlyContinue }
    if ($impl) { $env:FACTORY_IMPL = $impl } else { Remove-Item Env:FACTORY_IMPL -ErrorAction SilentlyContinue }
    $c = New-PesterConfiguration
    $c.Run.Path = $path
    $c.Run.PassThru = $true
    $c.Filter.FullName = $filter
    $c.Output.Verbosity = 'None'
    if ($resultFile) { $c.TestResult.Enabled = $true; $c.TestResult.OutputPath = $resultFile }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-Pester -Configuration $c
    [pscustomobject]@{
        Passed = $r.PassedCount; Failed = $r.FailedCount; Skipped = $r.SkippedCount; Seconds = [int]$sw.Elapsed.TotalSeconds
        FailedTests = @($r.Failed | ForEach-Object { $_.ExpandedPath })
        # A failed run with no failed test is a block or the file's own BeforeAll/AfterAll erroring:
        # name what threw, since the verbosity is None.
        RunError = if ($r.Result -eq 'Failed' -and $r.FailedCount -eq 0) {
            $errs = @(@($r.FailedBlocks) + @($r.FailedContainers) | ForEach-Object { $_.ErrorRecord } | Where-Object { $_ } | ForEach-Object { "$($_.Exception.Message)" })
            "$($r.FailedBlocksCount) block(s) and $($r.FailedContainersCount) file(s) failed to run: $(if ($errs) { $errs -join ' | ' } else { 'no error recorded' })"
        } else { $null }
    }
}

$total = [Diagnostics.Stopwatch]::StartNew()
$pending = [Collections.Generic.Queue[object]]::new([object[]]$units)
$running = @{}
$results = @()
$n = 0
while ($pending.Count -or $running.Count) {
    while ($pending.Count -and $running.Count -lt $Throttle) {
        $u = $pending.Dequeue(); $n++
        $xml = if ($ResultDir) { Join-Path $ResultDir ("{0:000}-{1}-{2}{3}.xml" -f $n, ($u.Name -replace '\.Tests\.ps1$', ''), $u.State, $(if ($u.Impl) { "-$($u.Impl)" })) }
        $job = Start-Job -ScriptBlock $run -ArgumentList $u.File, $u.Filter, $u.State, $xml, $u.Impl -WorkingDirectory $repo
        $running[$job.Id] = $u
    }
    $done = Wait-Job -Job @(Get-Job -Id @($running.Keys)) -Any
    $u = $running[$done.Id]; $running.Remove($done.Id)
    $r = Receive-Job $done -ErrorAction SilentlyContinue | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['Passed'] } | Select-Object -Last 1
    Remove-Job $done -Force
    if (-not $r) { $r = [pscustomobject]@{ Passed = 0; Failed = 0; Skipped = 0; Seconds = 0; FailedTests = @(); RunError = 'no result (the job died)' } }
    $ran = $r.Passed + $r.Failed + $r.Skipped
    $err = if ($r.RunError) { $r.RunError } elseif ($ran -ne $u.Tests) { "ran $ran of the $($u.Tests) tests discovery found" }
    $results += [pscustomobject]@{ Unit = $u; Passed = $r.Passed; Failed = $r.Failed; Skipped = $r.Skipped; Seconds = $r.Seconds; FailedTests = $r.FailedTests; RunError = $err }
    $mark = if ($r.Failed -or $err) { 'FAIL' } else { 'ok  ' }
    Write-Host ("{0} {1,4}s {2,4} passed {3,3} failed  {4}" -f $mark, $r.Seconds, $r.Passed, $r.Failed, $u.Label)
}

# The next run starts the longest units first.
# Merged, so a shard keeps the other shards' times.
try { foreach ($x in $results) { $lastTimes[$x.Unit.Label] = $x.Seconds }; Set-Content $timesPath ($lastTimes | ConvertTo-Json) } catch { }

Write-Host ''
foreach ($s in 'local', 'azure') {
    $rs = @($results | Where-Object { $_.Unit.State -eq $s })
    if (-not $rs) { continue }
    Write-Host ("{0}: passed {1} failed {2} skipped {3} (discovery found {4})" -f $s, ($rs | Measure-Object Passed -Sum).Sum, ($rs | Measure-Object Failed -Sum).Sum, ($rs | Measure-Object Skipped -Sum).Sum, $expected[$s])
}
# Minutes may pass 60: a timespan's mm format would wrap them.
$fmt = { param([timespan] $t) '{0}:{1:00}' -f [int][Math]::Floor($t.TotalMinutes), $t.Seconds }
if ($Shard) { Write-Host "shard $Shard" }
Write-Host ("{0} units, throttle {1}, wall {2}, units summed {3}" -f $units.Count, $Throttle, (& $fmt $total.Elapsed), (& $fmt ([timespan]::FromSeconds(($results | Measure-Object Seconds -Sum).Sum))))
$bad = @($results | Where-Object { $_.Failed -or $_.RunError })
foreach ($b in $bad) {
    if ($b.RunError) { Write-Host "  $($b.Unit.Label): $($b.RunError)" }
    foreach ($t in $b.FailedTests) { Write-Host "  $($b.Unit.Label): $t" }
}
exit $(if ($bad) { 1 } else { 0 })

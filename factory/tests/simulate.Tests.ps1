# The dashboard tests start with simulate.ps1 -Reset, while serve.ps1 is live on the same state
# directory. 3 of 50 runs failed with "The directory is not empty" (see docs/PLAN-PR1-PROOFS.md).
BeforeAll {
    . (Join-Path $PSScriptRoot 'fixtures/portable.ps1')   # -WindowStyle off Windows
}

Describe 'simulate.ps1 -Reset' {
    It 'waits out something still holding the state directory, instead of failing the run' {
        $state = Join-Path $TestDrive 'sim'
        New-Item -ItemType Directory $state | Out-Null
        $ready = Join-Path $TestDrive 'held'
        $hold = "`$f = [IO.File]::Open('$state/busy.txt', 'Create', 'Write', 'None'); New-Item '$ready' | Out-Null; Start-Sleep 4; `$f.Close()"
        $holder = Start-Process pwsh -ArgumentList '-NoProfile', '-Command', $hold -PassThru -WindowStyle Hidden
        while (-not (Test-Path $ready)) { Start-Sleep -Milliseconds 50 }

        $saved = $env:FACTORY_ENV_FILE
        $env:FACTORY_ENV_FILE = $null
        try { & pwsh -NoProfile -File (Join-Path $PSScriptRoot '../scripts/simulate.ps1') -Reset -Speed 200 -StateDir $state *> $null }
        finally { $env:FACTORY_ENV_FILE = $saved }
        $code = $LASTEXITCODE
        $holder.WaitForExit()

        $code | Should -Be 0
        Test-Path (Join-Path $state 'busy.txt') | Should -BeFalse
        Test-Path (Join-Path $state 'board.json') | Should -BeTrue
    }
}

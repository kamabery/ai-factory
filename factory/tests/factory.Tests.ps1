#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

BeforeAll {
    $script:Factory = Join-Path $PSScriptRoot '..\scripts\factory.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    . (Join-Path $PSScriptRoot 'fixtures\github.ps1')
    Start-GitHubStub

    function F {
        # Runs factory.ps1 in-process; stderr is discarded, $LASTEXITCODE carries the verdict.
        $out = & $script:Factory @args 2>$null
        $out
    }

    function Board { Get-Content (Join-Path $env:FACTORY_HOME 'board.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
    function Job([string]$id) { (Board).jobs | Where-Object { $_.id -eq $id } }
    function Events { Get-Content (Join-Path $env:FACTORY_HOME 'events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable -DateKind String } }
    function JobDir([string]$id) { Join-Path $env:FACTORY_HOME "jobs/$id" }

    function Spec-Text([string]$title, [string]$repo = $script:Repo, [string[]]$gate = @('build: `exit 0`')) {
        $t = "# Spec: $title`n`n## Intent`nDo $title.`n"
        if ($repo) { $t += "`n## Target`n- Repo: $repo`n" }
        if ($gate) { $t += "`n## Gate`n" + (($gate | ForEach-Object { "- $_" }) -join "`n") + "`n" }
        $t
    }

    function New-Spec([string]$title, [string]$repo = $script:Repo, [string[]]$gate = @('build: `exit 0`')) {
        $p = Join-Path $TestDrive "spec-$([guid]::NewGuid().ToString('N')).md"
        Set-Content $p (Spec-Text $title $repo $gate)
        $p
    }

    function Eval-File([string]$rating = 'Rating: mixed') {
        $p = Join-Path $TestDrive "eval-$([guid]::NewGuid().ToString('N')).md"
        Set-Content $p "$rating`n`n# Eval: the run"
        $p
    }
    # purge keeps a finished job until it is evaluated.
    function Evaluate([string[]]$ids) { foreach ($i in $ids) { F eval $i -File (Eval-File) | Out-Null } }

    function New-BacklogJob([string]$title = 'Thing', [string]$repo = $script:Repo, [string[]]$gate = @('build: `exit 0`')) {
        $idea = F new-idea "idea for $title" -Target $script:Repo
        F add-spec $idea (New-Spec $title $repo $gate)
    }

    function New-ApprovedJob([string]$title = 'Thing', [string]$repo = $script:Repo, [string[]]$gate = @('build: `exit 0`')) {
        $id = New-BacklogJob $title $repo $gate
        F approve $id | Out-Null
        $id
    }

    function Write-Build([string]$id) {
        $j = Job $id
        $dir = Join-Path (JobDir $id) "round-$($j.round)"
        New-Item -ItemType Directory -Force $dir | Out-Null
        Set-Content (Join-Path $dir 'build.md') "# Build: $id"
    }

    function Write-Reviews([string]$id, [hashtable]$verdicts) {
        $j = Job $id
        $dir = Join-Path (JobDir $id) "round-$($j.round)"
        New-Item -ItemType Directory -Force $dir | Out-Null
        foreach ($role in $verdicts.Keys) {
            Set-Content (Join-Path $dir "review-$role.md") "$($verdicts[$role])`n`n# $role review"
        }
    }

    function All([string]$v) { @{ security = $v; code = $v; ux = $v; ui = $v } }

    function Move-ToReview([string]$id) {
        F set-stage $id build -By runner | Out-Null
        Write-Build $id
        F set-stage $id review | Out-Null
    }

    function Move-ToApprove([string]$id) {
        Move-ToReview $id
        Write-Reviews $id (All 'Verdict: PASS')
        F read-reviews $id | Out-Null
    }

    function Move-ToGate([string]$id) {
        Move-ToApprove $id
        Set-Content (Join-Path (JobDir $id) 'decision.md') "Decision: APPROVE`n"
        F read-decision $id | Out-Null
    }

    function Commit-OnBranch([string]$id, [string]$file = 'feature.txt') {
        $wt = F checkout $id
        Set-Content (Join-Path $wt $file) "work for $id"
        git -C $wt add -A 2>&1 | Out-Null
        git -C $wt -c user.email=f@t -c user.name=f commit -q -m "work for $id" 2>&1 | Out-Null
    }

    # The human merging the job's PR on GitHub, outside the factory.
    function Merge-OnGitHub([string]$id) {
        Invoke-RestMethod -Method Put "http://localhost:$script:GhPort/repos/$script:Repo/pulls/$((Job $id).pr.number)/merge" `
            -Headers @{ Authorization = "Bearer $script:Token" } -ContentType 'application/json' -Body '{}' | Out-Null
    }

    function Gate-Cmds([int]$build = 0, [int]$test = 0, [int]$ui = 0) {
        ConvertTo-Json -Compress @(
            @{ name = 'build'; cmd = "exit $build" }
            @{ name = 'test'; cmd = "exit $test" }
            @{ name = 'ui'; cmd = "exit $ui" }
        )
    }
}

AfterAll {
    Stop-GitHubStub
}

Describe 'factory.ps1' {

BeforeEach {
    $script:Repo = "test/repo-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Remote $script:Repo

    $env:FACTORY_HOME = Join-Path $TestDrive "state-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $env:FACTORY_WORK = Join-Path $TestDrive "work-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $env:FACTORY_ENV_FILE = $script:EnvFile
    $env:FACTORY_GATE_CMDS = Gate-Cmds
}

AfterEach {
    Remove-Item Env:FACTORY_HOME, Env:FACTORY_WORK, Env:FACTORY_ENV_FILE, Env:FACTORY_GATE_CMDS -ErrorAction SilentlyContinue
}

Describe 'ideas and specs' {
    It 'allocates I-001, then J-001 and J-002 in backlog, titled from the spec heading' {
        $idea = F new-idea 'find books fast' -Target $script:Repo
        $idea | Should -Be 'I-001'
        Get-Content (Join-Path $env:FACTORY_HOME 'ideas/I-001/idea.md') -Raw | Should -Match 'find books fast'
        (Board).ideas[0].status | Should -Be 'drafting'

        F add-spec $idea (New-Spec 'Book search filter') | Should -Be 'J-001'
        F add-spec $idea (New-Spec 'J-999 — Sort by name') | Should -Be 'J-002'

        (Job 'J-001').stage | Should -Be 'backlog'
        (Job 'J-001').title | Should -Be 'Book search filter'
        (Job 'J-002').title | Should -Be 'Sort by name'
        (Job 'J-002').idea | Should -Be 'I-001'
        (Board).ideas[0].jobs | Should -Be @('J-001', 'J-002')
        Get-Content (Join-Path (JobDir 'J-001') 'spec.md') -First 1 | Should -Be '# Spec: J-001 — Book search filter'
    }

    It 'idea-done closes an idea, and -Failed keeps the reason' {
        $a = F new-idea 'good' -Target $script:Repo
        $b = F new-idea 'vague' -Target $script:Repo
        F idea-done $a | Out-Null
        F idea-done $b -Failed -Reason 'too vague to spec' | Out-Null
        $LASTEXITCODE | Should -Be 0
        ((Board).ideas | Where-Object id -eq $a).status | Should -Be 'done'
        $failed = (Board).ideas | Where-Object id -eq $b
        $failed.status | Should -Be 'failed'
        $failed.reason | Should -Be 'too vague to spec'
    }

    It 'edit-spec replaces spec.md in backlog and is refused in any other stage' {
        $id = New-BacklogJob
        $new = New-Spec 'edited'
        F edit-spec $id -File $new | Out-Null
        $LASTEXITCODE | Should -Be 0
        Get-Content (Join-Path (JobDir $id) 'spec.md') -Raw | Should -Match 'edited'

        F approve $id | Out-Null
        Set-Content $new '# Spec: too late'
        F edit-spec $id -File $new | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        Get-Content (Join-Path (JobDir $id) 'spec.md') -Raw | Should -Not -Match 'too late'
    }
}

Describe 'providers' {
    It 'add-spec stamps provider subscription when .env sets no FACTORY_PROVIDER' {
        (Job (New-BacklogJob)).provider | Should -Be 'subscription'
    }

    It 'add-spec stamps FACTORY_PROVIDER from .env, and refuses an unknown one' {
        $envFile = Join-Path $TestDrive "provider-$([guid]::NewGuid().ToString('N')).env"
        Set-Content $envFile ((Get-Content $script:EnvFile) + 'FACTORY_PROVIDER=openrouter')
        $env:FACTORY_ENV_FILE = $envFile
        (Job (New-BacklogJob)).provider | Should -Be 'openrouter'

        Set-Content $envFile ((Get-Content $script:EnvFile) + 'FACTORY_PROVIDER=bedrock')
        $idea = F new-idea 'idea' -Target $script:Repo
        F add-spec $idea (New-Spec 'Bad provider') | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        @((Board).jobs | Where-Object title -eq 'Bad provider') | Should -HaveCount 0
    }

    It 'set-provider changes the provider in backlog and approved, with a note event' {
        $id = New-BacklogJob
        F set-provider $id openrouter | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).provider | Should -Be 'openrouter'
        F approve $id | Out-Null
        F set-provider $id subscription | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).provider | Should -Be 'subscription'
        (Events | Where-Object { $_.job -eq $id -and $_.kind -eq 'note' }).detail | Should -Contain 'provider openrouter'
    }

    It 'set-provider refuses an unknown provider and any stage after approved' {
        $id = New-BacklogJob
        F set-provider $id bedrock | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).provider | Should -Be 'subscription'

        F approve $id | Out-Null
        F set-stage $id build -By runner | Out-Null
        F set-provider $id openrouter | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).provider | Should -Be 'subscription'
    }

    It 'the cost event carries the job''s provider, and subscription for a job from before providers' {
        $id = New-BacklogJob
        F set-provider $id openrouter | Out-Null
        F set-cost $id 0.1 100 | Out-Null
        @(Events | Where-Object { $_.job -eq $id -and $_.kind -eq 'cost' })[-1].provider | Should -Be 'openrouter'

        $old = New-BacklogJob 'Old'
        $b = Board
        ($b.jobs | Where-Object id -eq $old).Remove('provider')
        Set-Content (Join-Path $env:FACTORY_HOME 'board.json') ($b | ConvertTo-Json -Depth 10)
        F set-cost $old 0.1 100 | Out-Null
        @(Events | Where-Object { $_.job -eq $old -and $_.kind -eq 'cost' })[-1].provider | Should -Be 'subscription'
    }
}

Describe 'board writes' {
    It 'waits out a reader holding board.json (the dashboard poll) instead of failing the save' {
        $id = New-BacklogJob
        $path = Join-Path $env:FACTORY_HOME 'board.json'
        # As serve.ps1's ReadAllBytes opens it: read, shared for read only, so a replace is denied.
        $fs = [IO.File]::Open($path, 'Open', 'Read', 'Read')
        try {
            $p = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $script:Factory, 'approve', $id -PassThru -WindowStyle Hidden
            Start-Sleep -Milliseconds 800
        } finally { $fs.Dispose() }
        $p.WaitForExit()
        $p.ExitCode | Should -Be 0
        (Job $id).stage | Should -Be 'approved'
    }
}

Describe 'illegal transitions' {
    It 'refuses <from> -> <to> and leaves the board byte-for-byte unchanged' -ForEach @(
        @{ from = 'backlog'; to = 'done' }
        @{ from = 'backlog'; to = 'build' }
        @{ from = 'approved'; to = 'ready' }
    ) {
        $id = New-BacklogJob
        if ($from -eq 'approved') { F approve $id | Out-Null }
        $before = Get-Content (Join-Path $env:FACTORY_HOME 'board.json') -Raw
        F set-stage $id $to | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        Get-Content (Join-Path $env:FACTORY_HOME 'board.json') -Raw | Should -BeExactly $before
    }

    It 'refuses merge from backlog' {
        $id = New-BacklogJob
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'backlog'
    }
}

Describe 'queue' {
    It 'next returns the oldest approved job, and nothing while a job is building' {
        $a = New-ApprovedJob 'A'
        Start-Sleep -Milliseconds 20
        $b = New-ApprovedJob 'B'
        F next | Should -Be $a

        F set-stage $a build -By runner | Out-Null
        F next | Should -BeNullOrEmpty
    }

    It 'refuses approved -> build for a second job while one is building' {
        $a = New-ApprovedJob 'A'
        Start-Sleep -Milliseconds 20
        $b = New-ApprovedJob 'B'
        F set-stage $a build -By runner | Out-Null
        F set-stage $b build -By runner | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $b).stage | Should -Be 'approved'
    }

    It 'refuses approved -> build out of FIFO order' {
        $a = New-ApprovedJob 'A'
        Start-Sleep -Milliseconds 20
        $b = New-ApprovedJob 'B'
        F set-stage $b build -By runner | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }

    It 'refuses build -> review until round-<n>/build.md exists' {
        $id = New-ApprovedJob
        F set-stage $id build -By runner | Out-Null
        F set-stage $id review | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'build'
        Write-Build $id
        F set-stage $id review | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'review'
    }
}

Describe 'review routing' {
    It 'four PASS moves review -> approve' {
        $id = New-ApprovedJob
        Move-ToReview $id
        Write-Reviews $id (All 'Verdict: PASS')
        F read-reviews $id | Out-Null
        (Job $id).stage | Should -Be 'approve'
        (Job $id).reviews.security | Should -Be 'PASS'
    }

    It 'one CHANGES at round 1 goes back to build at round 2 with reviews reset' {
        $id = New-ApprovedJob
        Move-ToReview $id
        $v = All 'Verdict: PASS'; $v.ui = 'Verdict: CHANGES'
        Write-Reviews $id $v
        F read-reviews $id | Out-Null
        $j = Job $id
        $j.stage | Should -Be 'build'
        $j.round | Should -Be 2
        $j.reviews.Values | Sort-Object -Unique | Should -Be 'pending'
    }

    It 'CHANGES at round 2 lands in ready flagged max-rounds' {
        $id = New-ApprovedJob
        Move-ToReview $id
        Write-Reviews $id (All 'Verdict: CHANGES')
        F read-reviews $id | Out-Null
        Write-Build $id
        F set-stage $id review | Out-Null
        Write-Reviews $id (All 'Verdict: CHANGES')
        F read-reviews $id | Out-Null
        $j = Job $id
        $j.stage | Should -Be 'ready'
        $j.flag.reason | Should -Be 'max-rounds'
    }

    It 'a missing or malformed verdict counts as no verdict and the job does not move' {
        $id = New-ApprovedJob
        Move-ToReview $id
        Write-Reviews $id @{ security = 'Verdict: PASS'; code = 'verdict: pass'; ux = 'Looks good' }
        F read-reviews $id | Out-Null
        $LASTEXITCODE | Should -Be 2
        $j = Job $id
        $j.stage | Should -Be 'review'
        $j.reviews.security | Should -Be 'PASS'
        $j.reviews.code | Should -Not -Be 'PASS'
    }
}

Describe 'decision' {
    It 'APPROVE moves approve -> gate' {
        $id = New-ApprovedJob
        Move-ToGate $id
        (Job $id).stage | Should -Be 'gate'
        (Job $id).decision | Should -Be 'APPROVE'
    }

    It 'ESCALATE moves approve -> ready flagged escalated' {
        $id = New-ApprovedJob
        Move-ToApprove $id
        Set-Content (Join-Path (JobDir $id) 'decision.md') "Decision: ESCALATE`n"
        F read-decision $id | Out-Null
        (Job $id).stage | Should -Be 'ready'
        (Job $id).flag.reason | Should -Be 'escalated'
        (Job $id).flag.detail | Should -Be 'approver escalated; see decision.md'
    }

    It 'ESCALATE carries the escalation reason onto the flag' {
        $id = New-ApprovedJob
        Move-ToApprove $id
        Set-Content (Join-Path (JobDir $id) 'decision.md') @(
            'Decision: ESCALATE', '', '## Basis', 'AC1 evidenced.', '',
            '## Escalation reason', 'AC8 has no evidence; is that acceptable?', '', 'Options:', '1. Verify first.', '',
            '## Notes', 'not part of it')
        F read-decision $id | Out-Null
        $flag = (Job $id).flag
        $flag.detail | Should -Be 'AC8 has no evidence; is that acceptable?'
        $flag.why | Should -Match '(?s)^AC8 has no evidence.*1\. Verify first\.$'
        $flag.why | Should -Not -Match 'not part of it'
    }

    It 'a malformed decision does not move the job' {
        $id = New-ApprovedJob
        Move-ToApprove $id
        Set-Content (Join-Path (JobDir $id) 'decision.md') "I approve`n"
        F read-decision $id | Out-Null
        $LASTEXITCODE | Should -Be 2
        (Job $id).stage | Should -Be 'approve'
    }

    It 'any Building stage can be escalated to ready by the orchestrator' {
        $id = New-ApprovedJob
        F set-stage $id build -By runner | Out-Null
        F set-stage $id ready -Reason escalated -Detail 'builder gave up' | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).flag.detail | Should -Be 'builder gave up'
    }
}

Describe 'gate' {
    It 'green gate lands in ready with no flag and the branch sha recorded' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        $j = Job $id
        $j.stage | Should -Be 'ready'
        $j.flag | Should -BeNullOrEmpty
        $j.gate.test | Should -Be 0
        $j.gate.sha | Should -Be (git -C (Work $id) rev-parse "factory/$id")
    }

    It 'red gate lands in ready flagged gate-failed, with exit codes and gate.log' {
        $env:FACTORY_GATE_CMDS = Gate-Cmds -test 3
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        $j = Job $id
        $j.stage | Should -Be 'ready'
        $j.flag.reason | Should -Be 'gate-failed'
        $j.gate.build | Should -Be 0
        $j.gate.test | Should -Be 3
        Test-Path (Join-Path (JobDir $id) 'gate.log') | Should -BeTrue
    }

    It 'keeps UTF-8 output from gate commands intact in gate.log' {
        # npm/vite write raw UTF-8 bytes whatever the console codepage is.
        $text = "built $([char]0x2502) ok $([char]0x2713)"
        $emit = '$b = [Text.Encoding]::UTF8.GetBytes(''built '' + [char]0x2502 + '' ok '' + [char]0x2713); $s = [Console]::OpenStandardOutput(); $s.Write($b, 0, $b.Length); $s.Flush()'
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @(@{ name = 'ui'; cmd = $emit })
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        # The runner that calls the gate lives in serve.ps1's hidden console, which is on the OEM codepage.
        & pwsh -NoProfile -Command "[Console]::OutputEncoding = [Text.Encoding]::GetEncoding(437); & '$script:Factory' gate $id" 2>&1 | Out-Null
        (Job $id).gate.ui | Should -Be 0
        Get-Content (Join-Path (JobDir $id) 'gate.log') -Encoding utf8 | Should -Contain $text
    }

    It 'passes a native -name=value.ext argument whole, not split at the dot' {
        # J-022: pwsh handed terraform `-var-file=envs/ephemeral` and `.tfvars` as two arguments.
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @(@{ name = 'iac'; cmd = 'cmd /c echo -var-file=envs/ephemeral.tfvars; cmd /c echo ''-q=a.b''' })
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        $log = Get-Content (Join-Path (JobDir $id) 'gate.log')
        $log | Should -Contain '-var-file=envs/ephemeral.tfvars'
        $log | Should -Contain '-q=a.b'
    }
}

Describe 'gate-check' {
    BeforeEach {
        # A command that appends a line to a file outside the work tree, so a run is countable and the
        # tree stays clean.
        $script:Runs = Join-Path $TestDrive "runs-$([guid]::NewGuid().ToString('N').Substring(0,8)).txt"
        function Count-Cmd([string]$name, [int]$code = 0) { @{ name = $name; cmd = "Add-Content '$script:Runs' '$name'; 'ran $name'; exit $code" } }
        function Runs { @(Get-Content $script:Runs -ErrorAction SilentlyContinue) }
        function New-BuildingJob { $id = New-ApprovedJob; F set-stage $id build -By runner | Out-Null; Commit-OnBranch $id; $id }
    }

    It 'runs every command in the work dir, prints the output, and exits with the first non-zero code' {
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @(
            @{ name = 'where'; cmd = '(Get-Location).Path' }
            (Count-Cmd 'b' 3)
            (Count-Cmd 'c' 5)
        )
        $id = New-BuildingJob
        $out = F gate-check $id
        $LASTEXITCODE | Should -Be 3
        $out | Should -Contain (Resolve-Path (Work $id)).Path
        $out | Should -Contain 'ran b'
        $out | Should -Contain 'ran c'
        Runs | Should -Be @('b', 'c')
        Test-Path (Join-Path (JobDir $id) 'gate-check.log') | Should -BeTrue
    }

    It 'runs only the named command' {
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @((Count-Cmd 'a'), (Count-Cmd 'b' 2))
        $id = New-BuildingJob
        $out = F gate-check $id a
        $LASTEXITCODE | Should -Be 0
        $out | Should -Contain 'ran a'
        Runs | Should -Be @('a')
        F gate-check $id nope | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }

    It 'passes a native -name=value.ext argument whole, as gate does' {
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @(@{ name = 'iac'; cmd = 'cmd /c echo -var-file=envs/ephemeral.tfvars' })
        $id = New-BuildingJob
        F gate-check $id | Should -Contain '-var-file=envs/ephemeral.tfvars'
    }

    It 'leaves the board gate, stage, flag and events.jsonl unchanged' {
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @((Count-Cmd 'a' 1))
        $id = New-BuildingJob
        Write-Build $id
        F set-stage $id review | Out-Null
        $before = Job $id
        $events = @(Get-Content (Join-Path $env:FACTORY_HOME 'events.jsonl'))
        F gate-check $id | Out-Null
        $LASTEXITCODE | Should -Be 1
        $after = Job $id
        $after.stage | Should -Be 'review'
        $after.gate | Should -Be $before.gate
        $after.flag | Should -Be $before.flag
        @(Get-Content (Join-Path $env:FACTORY_HOME 'events.jsonl')) | Should -Be $events
    }

    It 'refuses a job in <stage>' -ForEach @(
        @{ stage = 'backlog' }, @{ stage = 'approved' }, @{ stage = 'ready' }, @{ stage = 'done' }
    ) {
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @((Count-Cmd 'a'))
        $id = if ($stage -eq 'backlog') { New-BacklogJob } else { New-ApprovedJob }
        if ($stage -in 'ready', 'done') {
            Commit-OnBranch $id
            Move-ToGate $id
            F gate $id | Out-Null
            Remove-Item $script:Runs -ErrorAction SilentlyContinue
        }
        if ($stage -eq 'done') { F merge $id | Out-Null }
        (Job $id).stage | Should -Be $stage
        F gate-check $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        Runs | Should -BeNullOrEmpty
    }

    It 'returns the cached result on the same head sha without re-running' {
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @((Count-Cmd 'a'), (Count-Cmd 'b' 4))
        $id = New-BuildingJob
        F gate-check $id | Out-Null
        $sha = (git -C (Work $id) rev-parse HEAD).Substring(0, 7)
        $out = F gate-check $id
        $LASTEXITCODE | Should -Be 4
        $out | Should -Contain 'ran b'
        ($out -join "`n") | Should -Match ([regex]::Escape("(cached at $sha)"))
        Runs | Should -Be @('a', 'b')
        # A new commit is a new head: it runs again.
        Commit-OnBranch $id 'more.txt'
        F gate-check $id a | Out-Null
        Runs | Should -Be @('a', 'b', 'a')
    }

    It 'does not cache a dirty work tree' {
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @((Count-Cmd 'a'))
        $id = New-BuildingJob
        Set-Content (Join-Path (Work $id) 'uncommitted.txt') 'wip'
        F gate-check $id | Out-Null
        $out = F gate-check $id
        ($out -join "`n") | Should -Not -Match 'cached at'
        Runs | Should -Be @('a', 'a')
    }

    It 'runs two concurrent calls one after the other' {
        $marks = Join-Path $TestDrive "marks-$([guid]::NewGuid().ToString('N').Substring(0,8)).txt"
        $slow = { param($n) @{ name = $n; cmd = "Add-Content '$marks' ""start $n `$([DateTime]::UtcNow.Ticks)""; Start-Sleep -Seconds 2; Add-Content '$marks' ""end $n `$([DateTime]::UtcNow.Ticks)""" } }
        $env:FACTORY_GATE_CMDS = ConvertTo-Json -Compress @((& $slow 'a'), (& $slow 'b'))
        $id = New-BuildingJob
        # Different names, so the second call cannot be served from the cache.
        $jobs = 'a', 'b' | ForEach-Object { Start-Job -ArgumentList $script:Factory, $id, $_ { param($f, $id, $n) & $f gate-check $id $n 2>&1 | Out-Null; $LASTEXITCODE } }
        $codes = $jobs | Wait-Job -Timeout 60 | Receive-Job
        $jobs | Remove-Job -Force
        $codes | Should -Be @(0, 0)
        $m = @(Get-Content $marks | ForEach-Object { $k, $n, $t = $_ -split ' '; [pscustomobject]@{ kind = $k; name = $n; t = [long]$t } } | Sort-Object t)
        $m.Count | Should -Be 4
        $m[0].kind | Should -Be 'start'
        $m[1].kind | Should -Be 'end'
        $m[1].name | Should -Be $m[0].name
        $m[2].kind | Should -Be 'start'
    }
}

Describe 'scope check' {
    BeforeEach {
        # J-022's Scope: In, prose and inline backticks after the glob included.
        $script:J022In = @(
            '  - `.aspire/apphost.cs` (Keycloak `#:package`, `Auth__*` on api)'
            '  - `.aspire/keycloak/**`'
            '  - `IaC/auth.*.tf`'
            '  - the generated lockfile, wherever npm puts it'
        )
        function New-ScopedJob([string[]]$in) {
            $idea = F new-idea 'scoped' -Target $script:Repo
            $p = Join-Path $TestDrive "spec-$([guid]::NewGuid().ToString('N')).md"
            Set-Content $p ((Spec-Text 'Scoped') + "`n## Scope`n- In:`n$($in -join "`n")`n- Out: the API's controllers`n")
            $id = F add-spec $idea $p
            F approve $id | Out-Null
            F set-stage $id build -By runner | Out-Null
            F checkout $id | Out-Null
            $id
        }
        function Commit-Paths([string]$id, [string[]]$paths) {
            $wt = Work $id
            foreach ($f in $paths) {
                $full = Join-Path $wt $f
                New-Item -ItemType Directory -Force (Split-Path $full -Parent) | Out-Null
                Set-Content $full "work on $f"
            }
            git -C $wt add -A 2>&1 | Out-Null
            git -C $wt -c user.email=f@t -c user.name=f commit -q -m "work for $id" 2>&1 | Out-Null
        }
        function To-Review([string]$id) { Write-Build $id; F set-stage $id review }
    }

    It 'moves to review when every path matches an In glob; ** crosses segments and prose sub-bullets are ignored' {
        $id = New-ScopedJob $script:J022In
        Commit-Paths $id '.aspire/apphost.cs', '.aspire/keycloak/realm/clients.json', 'IaC/auth.main.tf'
        To-Review $id | Should -Be 'review'
        (Job $id).stage | Should -Be 'review'
    }

    It 'a stray path moves a non-deployable job to ready, flagged out-of-scope with every path in why' {
        $id = New-ScopedJob $script:J022In
        # * stays within one segment, and the inline `Auth__*` after the glob is not a glob.
        Commit-Paths $id '.aspire/apphost.cs', 'IaC/auth.x/y.tf', 'IaC/adopt.tf'
        To-Review $id | Should -Be 'ready'
        $j = Job $id
        $j.flag.reason | Should -Be 'out-of-scope'
        $j.flag.detail | Should -Be '2 paths outside Scope: In: IaC/adopt.tf, IaC/auth.x/y.tf'
        $j.flag.why | Should -Match 'IaC/adopt\.tf'
        $j.flag.why | Should -Match 'IaC/auth\.x/y\.tf'
        $j.flag.why | Should -Not -Match 'apphost'
    }

    It 'a stray path moves a deployable job to blocked' {
        Use-Deploys
        try {
            $id = New-ScopedJob $script:J022In
            Commit-Paths $id 'IaC/adopt.tf'
            To-Review $id | Should -Be 'blocked'
            (Job $id).flag.reason | Should -Be 'out-of-scope'
        } finally { Clear-Deploys }
    }

    It 'does not count base work merged into the branch' {
        $id = New-ScopedJob $script:J022In
        Push-ToMain 'base-only.txt' 'merged elsewhere'
        F checkout $id | Out-Null
        git -C (Work $id) -c user.email=f@t -c user.name=f merge -q --no-edit factory/base 2>&1 | Out-Null
        Test-Path (Join-Path (Work $id) 'base-only.txt') | Should -BeTrue
        Commit-Paths $id '.aspire/apphost.cs'
        To-Review $id | Should -Be 'review'
    }

    It 'a spec with no In globs moves to review with a note that it was not scope-checked' {
        $id = New-ScopedJob @()
        Commit-Paths $id 'anything.txt'
        To-Review $id | Should -Be 'review'
        (Events | Where-Object { $_.kind -eq 'note' -and $_.job -eq $id }).detail | Should -Contain 'not scope-checked: the spec has no Scope: In globs'
    }

    It 'rework with Scope + appends the globs to the spec as (rework n) sub-bullets, and the next round accepts them' {
        $id = New-ScopedJob $script:J022In
        Commit-Paths $id 'IaC/adopt.tf', 'docs/auth.md'
        To-Review $id | Out-Null
        (Job $id).flag.reason | Should -Be 'out-of-scope'

        F rework $id "Keep them.`nScope +: IaC/adopt.tf, ``docs/**``" | Out-Null
        $LASTEXITCODE | Should -Be 0
        $spec = Get-Content (Join-Path (JobDir $id) 'spec.md')
        $spec | Should -Contain '  - `IaC/adopt.tf` (rework 2)'
        $spec | Should -Contain '  - `docs/**` (rework 2)'
        # Inside Scope: In, before Out.
        $spec.IndexOf('  - `docs/**` (rework 2)') | Should -BeLessThan $spec.IndexOf("- Out: the API's controllers")
        (Events | Where-Object { $_.kind -eq 'note' -and $_.job -eq $id }).detail | Should -Contain 'scope widened (rework 2): IaC/adopt.tf, docs/**'

        F set-stage $id build -By runner | Out-Null
        To-Review $id | Should -Be 'review'
    }

    It 'rework with Scope + refuses a spec that has no Scope: In to widen' {
        $id = New-ScopedJob @()
        Commit-Paths $id 'x.txt'
        To-Review $id | Out-Null
        F set-stage $id ready -Reason escalated -Detail 'gave up' | Out-Null
        F rework $id "Scope +: x/**" | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'ready'
    }
}

Describe 'base moved' {
    BeforeEach {
        # J-A of one idea taken to ready (gate green, PR open); its siblings are added by each test.
        function New-Sibling([string]$idea, [string]$title) { F add-spec $idea (New-Spec $title) }
        function Ready-First {
            $script:Idea = F new-idea 'two parts' -Target $script:Repo
            $a = New-Sibling $script:Idea 'Part A'
            F approve $a | Out-Null
            Commit-OnBranch $a
            Move-ToGate $a
            F gate $a | Out-Null
            F publish $a | Out-Null
            $a
        }
        # A sibling taken to build after J-A merged, so it carries baseMoved.
        function Stale-Sibling {
            $a = Ready-First
            $b = New-Sibling $script:Idea 'Part B'
            F approve $b | Out-Null
            F merge $a | Out-Null
            F set-stage $b build -By runner | Out-Null
            Commit-OnBranch $b 'b.txt'
            $b
        }
        function Build-With([string]$id, [string]$section) {
            $dir = Join-Path (JobDir $id) "round-$((Job $id).round)"
            New-Item -ItemType Directory -Force $dir | Out-Null
            Set-Content (Join-Path $dir 'build.md') "# Build: $id`n`n## What changed`n- b.txt`n$section"
        }
    }

    It 'merge marks every open sibling of the same idea, and no finished one or other idea''s job' {
        $a = Ready-First
        $backlog = New-Sibling $script:Idea 'Backlog sibling'
        $approved = New-Sibling $script:Idea 'Approved sibling'
        F approve $approved | Out-Null
        $building = New-Sibling $script:Idea 'Building sibling'
        F approve $building | Out-Null
        $gone = New-Sibling $script:Idea 'Discarded sibling'
        F discard $gone | Out-Null
        $other = New-BacklogJob 'Another idea'
        # Building is the oldest approved after J-A, so let it through the queue.
        F set-stage $approved build -By runner | Out-Null

        F merge $a | Out-Null
        $LASTEXITCODE | Should -Be 0
        $sha = (Job $a).merged.sha
        foreach ($id in $backlog, $approved, $building) {
            $m = (Job $id).baseMoved
            $m.by | Should -Be $a
            $m.sha | Should -Be $sha
            $m.at | Should -Not -BeNullOrEmpty
            (Events | Where-Object { $_.kind -eq 'note' -and $_.job -eq $id }).detail | Should -Contain "base moved: $a merged"
        }
        (Job $gone).baseMoved | Should -BeNullOrEmpty
        (Job $other).baseMoved | Should -BeNullOrEmpty
        (Job $a).baseMoved | Should -BeNullOrEmpty
    }

    It 'a merge on GitHub, recorded by sync-prs, marks the open siblings too' {
        $a = Ready-First
        $b = New-Sibling $script:Idea 'Part B'
        Merge-OnGitHub $a
        F sync-prs | Out-Null
        (Job $a).stage | Should -Be 'done'
        (Job $b).baseMoved.by | Should -Be $a
        (Job $b).baseMoved.sha | Should -Be (Job $a).merged.sha
    }

    It 'set-stage review refuses with exit 2 when build.md has no Spec against base, and the job stays in build' {
        $b = Stale-Sibling
        Build-With $b ''
        F set-stage $b review | Out-Null
        $LASTEXITCODE | Should -Be 2
        (Job $b).stage | Should -Be 'build'

        Build-With $b "`n## Spec against base`nIt still fits.`n"
        F set-stage $b review | Out-Null
        $LASTEXITCODE | Should -Be 2
        (Job $b).stage | Should -Be 'build'
    }

    It 'Spec holds: yes moves to review and clears baseMoved' {
        $b = Stale-Sibling
        Build-With $b "`n## Spec against base`nSpec holds: yes`nPart A touched only feature.txt.`n"
        F set-stage $b review | Should -Be 'review'
        (Job $b).baseMoved | Should -BeNullOrEmpty
    }

    It 'Spec holds: no escalates spec-stale with the section as why, and keeps baseMoved' {
        $b = Stale-Sibling
        $section = "## Spec against base`nSpec holds: no`nPart A renamed the page this spec edits.`nScope +: src/pages/renamed/**"
        Build-With $b "`n$section`n"
        F set-stage $b review | Should -Be 'ready'
        $j = Job $b
        $j.flag.reason | Should -Be 'spec-stale'
        $j.flag.why | Should -Match 'Spec holds: no'
        $j.flag.why | Should -Match 'Scope \+: src/pages/renamed/\*\*'
        $j.baseMoved.by | Should -Not -BeNullOrEmpty
    }

    It 'a job without baseMoved needs no Spec against base' {
        $id = New-ApprovedJob
        Move-ToReview $id
        (Job $id).stage | Should -Be 'review'
    }

    It 'rework clears baseMoved' {
        $b = Stale-Sibling
        Build-With $b "`n## Spec against base`nSpec holds: no`nIt does not.`n"
        F set-stage $b review | Out-Null
        F rework $b 'Build it against the new page.' | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $b).baseMoved | Should -BeNullOrEmpty
    }
}

Describe 'template provenance' {
    BeforeEach {
        function Commit-Files([string]$id, [hashtable]$files, [string]$subject) {
            $wt = Work $id
            foreach ($f in $files.Keys) {
                $full = Join-Path $wt $f
                New-Item -ItemType Directory -Force (Split-Path $full -Parent) | Out-Null
                Set-Content $full $files[$f]
            }
            git -C $wt add -A 2>&1 | Out-Null
            git -C $wt -c user.email=f@t -c user.name=f commit -q -m $subject 2>&1 | Out-Null
        }
        # A target bootstrapped by the factory: J-boot merged with src/auth.ts and app.slnx from the scaffold.
        function New-Bootstrapped {
            $t = "test/prov-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $idea, $boot = (F new-idea 'a todo app' -Target $t -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false}') -split ' '
            F approve $boot | Out-Null
            F checkout $boot | Out-Null
            Commit-Files $boot @{ 'app.slnx' = '<Solution />'; 'src/auth.ts' = 'export const auth = 1' } 'scaffold: dotnet new aspire-base'
            Move-ToGate $boot
            F gate $boot | Out-Null
            F merge $boot | Out-Null
            @{ Target = $t; Idea = $idea; Boot = $boot }
        }
        # A feature job on $target at round 2: round 1 added feature.txt and an add-on's files.
        function Round-Two([string]$target, [string]$idea) {
            $id = F add-spec $idea (New-Spec 'Feature' $target)
            F approve $id | Out-Null
            F set-stage $id build -By runner | Out-Null
            F checkout $id | Out-Null
            Commit-Files $id @{ 'feature.txt' = 'v1' } "$($id): feature"
            Commit-Files $id @{ 'src/addon/handler.cs' = 'class H {}' } 'addon: auth'
            Write-Build $id
            F set-stage $id review | Out-Null
            Write-Reviews $id (All 'Verdict: CHANGES')
            F read-reviews $id | Out-Null
            F checkout $id | Out-Null
            $id
        }
        function Touch-All([string]$id) {
            Commit-Files $id @{ 'feature.txt' = 'v2'; 'src/auth.ts' = 'export const auth = 2'; 'src/addon/handler.cs' = 'class H { }' } "$($id): round 2 fixes"
        }
        function Build-Gaps([string]$id, [string]$gaps) {
            $dir = Join-Path (JobDir $id) "round-$((Job $id).round)"
            New-Item -ItemType Directory -Force $dir | Out-Null
            Set-Content (Join-Path $dir 'build.md') "# Build: $id`n$(if ($gaps) { "`n## Template gaps`n$gaps`n" })"
        }
        # A child process: factory.ps1 writes its messages with [Console]::Error, which 2>&1 cannot catch in-process.
        function FErr { pwsh -NoProfile -File $script:Factory @args 2>&1 | ForEach-Object { "$_" } }
    }

    It 'checkout records roundStart once per round, and a move into build clears it' {
        $id = New-ApprovedJob
        F set-stage $id build -By runner | Out-Null
        F checkout $id | Out-Null
        $start = git -C (Work $id) rev-parse HEAD
        (Job $id).roundStart | Should -Be $start
        Commit-OnBranch $id
        (Job $id).roundStart | Should -Be $start        # a second checkout in the same round keeps it
        Write-Build $id
        F set-stage $id review | Out-Null
        Write-Reviews $id (All 'Verdict: CHANGES')
        F read-reviews $id | Out-Null
        (Job $id).roundStart | Should -BeNullOrEmpty
        F checkout $id | Out-Null
        (Job $id).roundStart | Should -Be (git -C (Work $id) rev-parse HEAD)
    }

    It 'provenance lists this round''s paths from the bootstrap merge or an addon: commit, not ordinary ones' {
        $b = New-Bootstrapped
        $id = Round-Two $b.Target $b.Idea
        Touch-All $id
        $out = @(F provenance $id)
        $LASTEXITCODE | Should -Be 0
        $out | Should -Be @('src/addon/handler.cs', 'src/auth.ts')
    }

    It 'provenance counts only the round''s own commits, not base work merged in' {
        $b = New-Bootstrapped
        $id = Round-Two $b.Target $b.Idea
        # Another change to a scaffold file lands on main and is merged into the branch this round.
        $c = Join-Path $TestDrive "other-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        git clone -q (Bare $b.Target) $c 2>&1 | Out-Null
        Set-Content (Join-Path $c 'app.slnx') '<Solution Name="x" />'
        git -C $c add -A 2>&1 | Out-Null
        git -C $c -c user.email=o@t -c user.name=o commit -q -m 'other: app.slnx' 2>&1 | Out-Null
        git -C $c push -q origin main 2>&1 | Out-Null
        F checkout $id | Out-Null
        git -C (Work $id) -c user.email=f@t -c user.name=f merge -q --no-edit factory/base 2>&1 | Out-Null
        Commit-Files $id @{ 'feature.txt' = 'v2' } "$($id): round 2"
        @(F provenance $id) | Should -BeNullOrEmpty
    }

    It 'takes the bootstrap''s merge sha from evals/ once the bootstrap job is purged' {
        $b = New-Bootstrapped
        Evaluate @($b.Boot)
        F purge | Out-Null
        (Board).jobs | Where-Object id -eq $b.Boot | Should -BeNullOrEmpty
        $idea = F new-idea 'more' -Target $b.Target
        $id = Round-Two $b.Target $idea
        Touch-All $id
        @(F provenance $id) | Should -Contain 'src/auth.ts'
    }

    It 'with no bootstrap on record, says so and still counts addon: commits' {
        $idea = F new-idea 'on an existing repo' -Target $script:Repo
        $id = Round-Two $script:Repo $idea
        Commit-Files $id @{ 'README.md' = 'changed'; 'src/addon/handler.cs' = 'class H { }' } "$($id): round 2"
        $out = FErr provenance $id
        $out | Should -Contain 'src/addon/handler.cs'
        $out | Should -Not -Contain 'README.md'
        ($out -join "`n") | Should -Match "no bootstrap merge on record for $([regex]::Escape($script:Repo))"
    }

    It 'set-stage review in round 2 exits 2 and names each template-sourced path build.md''s Template gaps leaves out' {
        $b = New-Bootstrapped
        $id = Round-Two $b.Target $b.Idea
        Touch-All $id
        Build-Gaps $id ''
        $out = (FErr set-stage $id review) -join "`n"
        $LASTEXITCODE | Should -Be 2
        $out | Should -Match 'src/auth\.ts'
        $out | Should -Match 'src/addon/handler\.cs'
        (Job $id).stage | Should -Be 'build'

        Build-Gaps $id "- Symptom: sign-in swallowed errors / Cause: src/auth.ts voids the redirect / Fix applied here: catch it / Belongs in ``template-projects:addons/auth/src/auth.ts``"
        $out = (FErr set-stage $id review) -join "`n"
        $LASTEXITCODE | Should -Be 2
        $out | Should -Match 'src/addon/handler\.cs'
        $out | Should -Not -Match 'src/auth\.ts'

        Build-Gaps $id "- Symptom: x / Cause: src/auth.ts / Fix applied here: y / Belongs in ``template-projects:addons/auth/src/auth.ts```n- src/addon/handler.cs: job-specific, this app's own handler"
        F set-stage $id review | Should -Be 'review'
    }

    It 'round 1 is not provenance-checked' {
        $b = New-Bootstrapped
        $id = F add-spec $b.Idea (New-Spec 'First' $b.Target)
        F approve $id | Out-Null
        F set-stage $id build -By runner | Out-Null
        F checkout $id | Out-Null
        Commit-Files $id @{ 'src/auth.ts' = 'changed in round 1' } "$($id): round 1"
        Build-Gaps $id ''
        F set-stage $id review | Should -Be 'review'
    }

    It 'lesson compiles build.md template gaps and skips job-specific lines and None' {
        $id = New-ApprovedJob 'Sign-in'
        Move-ToReview $id
        Build-Gaps $id "- Symptom: AADSTS500011 / Cause: identifier_uris cleared / Fix applied here: ignore_changes / Belongs in ``template-projects:addons/auth/IaC/auth.entra-external.tf```n- src/app.cs: job-specific, the app's own route"
        F set-stage $id ready -Reason escalated -Detail 'stop' | Out-Null
        F lesson $id | Out-Null
        $l = Get-Content (Join-Path $env:FACTORY_HOME "lessons/$id.json") -Raw | ConvertFrom-Json -AsHashtable
        $l.templateGaps | Should -Be 1
        $md = Get-Content (Join-Path $env:FACTORY_HOME "lessons/$id.md") -Raw
        $md | Should -Match 'AADSTS500011'
        $md | Should -Not -Match 'job-specific'

        $id2 = New-ApprovedJob 'Quiet'
        Move-ToGate $id2
        Build-Gaps $id2 'None.'
        F gate $id2 | Out-Null
        F lesson $id2 | Should -Be 'no lessons'
    }
}

Describe 'publish and merge' {
    It 'publish pushes the branch and opens one PR into main, and is idempotent' {
        $id = New-ApprovedJob 'Ship it'
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        $url = F publish $id
        $LASTEXITCODE | Should -Be 0
        $url | Should -Match '/pull/1$'
        $j = Job $id
        $j.pr.number | Should -Be 1
        $j.pr.url | Should -Be $url
        RGit rev-parse "factory/$id" | Should -Be (git -C (Work $id) rev-parse HEAD)
        F publish $id | Should -Be $url
        @(GhRequests | Where-Object { $_.method -eq 'POST' -and $_.path -match '/pulls$' }).Count | Should -Be 1
        $pr = (GhRequests | Where-Object { $_.method -eq 'POST' -and $_.path -match '/pulls$' }).body
        $pr.base | Should -Be 'main'
        $pr.head | Should -Be "factory/$id"
        $pr.title | Should -Be "$($id): Ship it"
    }

    It 'publish is refused outside ready' {
        $id = New-ApprovedJob
        F publish $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }

    It 'green gate: merges the PR --no-ff into main, deletes the branch and the work dir, job done' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        $j = Job $id
        $j.stage | Should -Be 'done'
        RGit rev-list --parents -n 1 main | ForEach-Object { ($_ -split ' ').Count } | Should -Be 3
        RGit show main:feature.txt | Should -Be "work for $id"
        $j.merged.sha | Should -Be (RGit rev-parse main)
        $j.merged.repo | Should -Be $script:Repo
        $j.merged.pr | Should -Be $j.pr.url
        $j.merged.added | Should -Be @('feature.txt')
        RGit branch --list "factory/$id" | Should -BeNullOrEmpty
        Test-Path (Work $id) | Should -BeFalse
        (Events | Where-Object kind -eq 'merge').job | Should -Be $id
        $m = GhRequests | Where-Object { $_.method -eq 'PUT' -and $_.path -match '/merge$' }
        $m.body.merge_method | Should -Be 'merge'
        $m.body.sha | Should -Be $j.gate.sha
    }

    It 'uncommitted changes in the work dir: stays ready flagged merge-blocked, nothing merged' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        $head = RGit rev-parse main
        Add-Content (Join-Path (Work $id) 'README.md') 'dirty'
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'ready'
        (Job $id).flag.reason | Should -Be 'merge-blocked'
        (Job $id).flag.detail | Should -Match 'README\.md'
        RGit rev-parse main | Should -Be $head
        Test-Path (Work $id) | Should -BeTrue
    }

    It 'a PR GitHub will not merge (conflict) stays ready flagged merge-blocked with its message' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        Push-ToMain 'feature.txt' 'theirs'
        $head = RGit rev-parse main
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'ready'
        (Job $id).flag.reason | Should -Be 'merge-blocked'
        (Job $id).flag.detail | Should -Match 'not mergeable'
        (Job $id).merged | Should -BeNullOrEmpty
        RGit rev-parse main | Should -Be $head
        Test-Path (Work $id) | Should -BeTrue
    }

    It 'without a green gate for the head sha, merge runs the gate first (red → gate-failed)' {
        $env:FACTORY_GATE_CMDS = Gate-Cmds -ui 1
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToReview $id
        Write-Reviews $id (All 'Verdict: CHANGES'); F read-reviews $id | Out-Null
        Write-Build $id; F set-stage $id review | Out-Null
        Write-Reviews $id (All 'Verdict: CHANGES'); F read-reviews $id | Out-Null
        (Job $id).flag.reason | Should -Be 'max-rounds'
        $head = RGit rev-parse main

        F merge $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'ready'
        (Job $id).flag.reason | Should -Be 'gate-failed'
        RGit rev-parse main | Should -Be $head
    }

    It 'without a gate, merge runs it first (green → merged)' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        F set-stage $id build -By runner | Out-Null
        F set-stage $id ready -Reason escalated -Detail 'x' | Out-Null
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'done'
        (Job $id).gate.sha | Should -Not -BeNullOrEmpty
    }

    It 'a PR the human merged on GitHub: merge records that merge and moves the job to done, merging nothing again' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        F publish $id | Out-Null
        Merge-OnGitHub $id
        $sha = RGit rev-parse main
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        $j = Job $id
        $j.stage | Should -Be 'done'
        $j.flag | Should -BeNullOrEmpty
        $j.merged.sha | Should -Be $sha
        $j.merged.pr | Should -Be $j.pr.url
        $j.merged.added | Should -Be @('feature.txt')
        RGit rev-parse main | Should -Be $sha
        @(GhRequests | Where-Object { $_.method -eq 'PUT' -and $_.path -match '/merge$' }).Count | Should -Be 1   # only the human's
        Test-Path (Work $id) | Should -BeFalse
        (Events | Where-Object kind -eq 'merge').detail | Should -Be $sha
    }

    It 'sync-prs moves each ready job whose PR was merged on GitHub to done, and leaves the others' {
        $a = New-ApprovedJob 'A'
        Commit-OnBranch $a 'a.txt'; Move-ToGate $a; F gate $a | Out-Null; F publish $a | Out-Null
        $b = New-ApprovedJob 'B'
        Commit-OnBranch $b 'b.txt'; Move-ToGate $b; F gate $b | Out-Null; F publish $b | Out-Null
        Merge-OnGitHub $a
        F sync-prs | Should -Be $a
        $LASTEXITCODE | Should -Be 0
        (Job $a).stage | Should -Be 'done'
        (Job $b).stage | Should -Be 'ready'
        F sync-prs | Should -BeNullOrEmpty
    }

    It 'a PR merged on GitHub at a head the gate never passed still lands in done, with a note saying so' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        F publish $id | Out-Null
        # A commit pushed to the PR branch on GitHub after the gate.
        $c = Join-Path $TestDrive "pr-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        git clone -q -b "factory/$id" (Bare) $c 2>&1 | Out-Null
        Set-Content (Join-Path $c 'late.txt') 'late'
        git -C $c add -A 2>&1 | Out-Null
        git -C $c -c user.email=o@t -c user.name=o commit -q -m late 2>&1 | Out-Null
        git -C $c push -q origin "factory/$id" 2>&1 | Out-Null
        Merge-OnGitHub $id
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'done'
        (Events | Where-Object { $_.kind -eq 'note' -and $_.job -eq $id } | Select-Object -Last 1).detail | Should -Match 'merged on GitHub at \w{7}, not the gated'
    }

    It 'merge refuses a branch that adds nothing to main, and records no merge' {
        $id = New-ApprovedJob 'Empty'
        F checkout $id | Out-Null
        Move-ToGate $id
        F gate $id | Out-Null
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        $j = Job $id
        $j.stage | Should -Be 'ready'
        $j.flag.reason | Should -Be 'merge-blocked'
        $j.flag.detail | Should -Match 'nothing to merge'
        $j.merged | Should -BeNullOrEmpty
    }

    It 'the token goes to the API as a bearer token and never lands on disk' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        F publish $id | Out-Null
        GhRequests | ForEach-Object { $_.auth | Should -Be "Bearer $script:Token" }
        Get-Content (Join-Path (Work $id) '.git/config') -Raw | Should -Not -Match $script:Token
        foreach ($f in Get-ChildItem $env:FACTORY_HOME -Recurse -File) { Get-Content $f.FullName -Raw | Should -Not -Match $script:Token }
    }
}

Describe 'human moves from ready' {
    It 'rework goes back to approved at round + 1 with the note on disk and the flag cleared' {
        $id = New-ApprovedJob
        F set-stage $id build -By runner | Out-Null
        F set-stage $id ready -Reason escalated -Detail 'x' | Out-Null
        F rework $id 'use the Input component' | Out-Null
        $j = Job $id
        $j.stage | Should -Be 'approved'
        $j.round | Should -Be 2
        $j.flag | Should -BeNullOrEmpty
        Get-Content (Join-Path (JobDir $id) 'rework-2.md') -Raw | Should -Match 'Input component'
    }

    It 'rework keeps the pushed branch: a missing work dir is cloned again from it' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        F set-stage $id build -By runner | Out-Null
        F set-stage $id ready -Reason escalated -Detail 'x' | Out-Null
        F publish $id | Out-Null
        $head = git -C (Work $id) rev-parse HEAD
        F rework $id 'more' | Out-Null
        Remove-Item -Recurse -Force (Work $id)
        $wt = F checkout $id
        $LASTEXITCODE | Should -Be 0
        git -C $wt branch --show-current | Should -Be "factory/$id"
        git -C $wt rev-parse HEAD | Should -Be $head
    }

    It 'discard works from backlog and from ready, and closes the PR, deletes the branch and the work dir' {
        $a = New-BacklogJob 'A'
        F discard $a | Out-Null
        (Job $a).stage | Should -Be 'discarded'

        $b = New-ApprovedJob 'B'
        Commit-OnBranch $b
        F set-stage $b build -By runner | Out-Null
        F set-stage $b ready -Reason escalated -Detail 'x' | Out-Null
        F publish $b | Out-Null
        F discard $b | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $b).stage | Should -Be 'discarded'
        RGit branch --list "factory/$b" | Should -BeNullOrEmpty
        Test-Path (Work $b) | Should -BeFalse
        (GhRequests | Where-Object { $_.method -eq 'PATCH' }).body.state | Should -Be 'closed'
    }

    It 'discard is refused from building stages' {
        $id = New-ApprovedJob
        F set-stage $id build -By runner | Out-Null
        F discard $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }
}

Describe 'stacked jobs' {
    BeforeAll {
        # A job in ready with its PR open: what a session can pick as a base.
        function New-ReadyJob([string]$title = 'Base', [string]$file = 'base.txt') {
            $id = New-ApprovedJob $title
            Commit-OnBranch $id $file
            Move-ToGate $id
            F gate $id | Out-Null
            F publish $id | Out-Null
            $id
        }
        function New-StackedJob([string]$base, [string]$title = 'Fix') {
            $idea = F new-idea "fix on $base" -Target $script:Repo -Base $base
            F add-spec $idea (New-Spec $title)
        }
        function Pr([int]$n) { Invoke-RestMethod "http://localhost:$script:GhPort/repos/$script:Repo/pulls/$n" -Headers @{ Authorization = "Bearer $script:Token" } }
    }

    It 'new-idea -Base records the base, and add-spec copies it to the job' {
        $base = New-ReadyJob
        $idea = F new-idea 'tag list in the main menu' -Target $script:Repo -Base $base
        $LASTEXITCODE | Should -Be 0
        ((Board).ideas | Where-Object id -eq $idea).base | Should -Be $base
        $id = F add-spec $idea (New-Spec 'Menu')
        (Job $id).base | Should -Be $base
    }

    It 'new-idea -Base refuses a job that is unknown, unpublished, finished, or on another target' {
        $ready = New-ReadyJob
        $unpublished = New-BacklogJob 'No PR'
        $other = "test/other-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Remote $other
        $n = @((Board).ideas).Count
        foreach ($b in 'J-999', $unpublished) {
            F new-idea 'x' -Target $script:Repo -Base $b | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
        }
        F new-idea 'x' -Target $other -Base $ready | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        F merge $ready | Out-Null
        F new-idea 'x' -Target $script:Repo -Base $ready | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        @((Board).ideas).Count | Should -Be $n
    }

    It 'clone-target -Base clones the base branch, and refuses a base that is not open on the target' {
        $base = New-ReadyJob
        $dir = Join-Path $TestDrive "sess-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        F clone-target $script:Repo $dir -Base $base | Should -Be $dir
        Test-Path (Join-Path $dir 'base.txt') | Should -BeTrue
        F clone-target $script:Repo $dir -Base 'J-999' | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }

    It 'checkout branches a stacked job from its base, and publish opens its PR into the base branch' {
        $base = New-ReadyJob
        $id = New-StackedJob $base
        F approve $id | Out-Null
        Commit-OnBranch $id 'fix.txt'
        Test-Path (Join-Path (Work $id) 'base.txt') | Should -BeTrue
        Move-ToGate $id
        F gate $id | Out-Null
        F publish $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Pr (Job $id).pr.number).base.ref | Should -Be "factory/$base"
    }

    It 'merge refuses a stacked job until its base is done; merging the base retargets its PR to main, and then it merges' {
        $base = New-ReadyJob
        $id = New-StackedJob $base
        F approve $id | Out-Null
        Commit-OnBranch $id 'fix.txt'
        Move-ToGate $id
        F gate $id | Out-Null
        F publish $id | Out-Null

        $mainHead = RGit rev-parse main
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'ready'
        RGit rev-parse main | Should -Be $mainHead

        F merge $base | Out-Null
        $LASTEXITCODE | Should -Be 0
        $pr = Pr (Job $id).pr.number
        $pr.state | Should -Be 'open'
        $pr.base.ref | Should -Be 'main'

        F merge $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'done'
        RGit show main:fix.txt | Should -Be "work for $id"
        (Job $id).merged.added | Should -Be @('fix.txt')
    }

    It 'a base merged on GitHub retargets its stacked PRs when sync-prs records it' {
        $base = New-ReadyJob
        $id = New-StackedJob $base
        F approve $id | Out-Null
        Commit-OnBranch $id 'fix.txt'
        Move-ToGate $id
        F gate $id | Out-Null
        F publish $id | Out-Null
        Merge-OnGitHub $base
        F sync-prs | Should -Be $base
        $pr = Pr (Job $id).pr.number
        $pr.state | Should -Be 'open'
        $pr.base.ref | Should -Be 'main'
    }

    It 'a stacked job checked out after its base merged starts from main, and its PR goes into main' {
        $base = New-ReadyJob
        $id = New-StackedJob $base
        F merge $base | Out-Null
        F approve $id | Out-Null
        Commit-OnBranch $id 'fix.txt'
        Test-Path (Join-Path (Work $id) 'base.txt') | Should -BeTrue
        Move-ToGate $id
        F gate $id | Out-Null
        F publish $id | Out-Null
        (Pr (Job $id).pr.number).base.ref | Should -Be 'main'
    }

    It 'discard refuses a base while a stacked job is live, and allows it once that job is discarded' {
        $base = New-ReadyJob
        $id = New-StackedJob $base
        F discard $base | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $base).stage | Should -Be 'ready'

        F discard $id | Out-Null
        F discard $base | Out-Null
        $LASTEXITCODE | Should -Be 0
    }
}

Describe 'abandon' {
    It 'idea-abandon hides a drafting, waiting or failed idea, leaves its jobs, and purge clears it' {
        $a = F new-idea 'drafting one' -Target $script:Repo
        $job = F add-spec $a (New-Spec 'Left alone')
        $b = F new-idea 'failed one' -Target $script:Repo
        F idea-done $b -Failed -Reason 'meh' | Out-Null
        foreach ($i in $a, $b) {
            F idea-abandon $i | Out-Null
            $LASTEXITCODE | Should -Be 0
            ((Board).ideas | Where-Object id -eq $i).status | Should -Be 'abandoned'
        }
        (Job $job).stage | Should -Be 'backlog'
        (Events | Where-Object { $_.kind -eq 'idea' -and $_.idea -eq $a }).detail | Should -Contain 'abandoned'

        F discard $job | Out-Null
        Evaluate $job
        $gone = F purge
        $gone | Should -Contain $a
        $gone | Should -Contain $b
    }

    It 'idea-abandon refuses a done or already abandoned idea' {
        $a = F new-idea 'done one' -Target $script:Repo
        F idea-done $a | Out-Null
        F idea-abandon $a | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        ((Board).ideas | Where-Object id -eq $a).status | Should -Be 'done'
    }
}

Describe 'purge' {
    It 'removes done and discarded jobs with their folders and branches, and keeps the rest' {
        $done = New-ApprovedJob 'D'
        Commit-OnBranch $done
        Move-ToGate $done
        F gate $done | Out-Null
        F merge $done | Out-Null
        (Job $done).stage | Should -Be 'done'
        $gone = New-BacklogJob 'X'
        F discard $gone | Out-Null
        $kept = New-BacklogJob 'K'
        Evaluate $done, $gone

        $out = F purge
        $LASTEXITCODE | Should -Be 0
        $out | Should -Contain $done
        $out | Should -Contain $gone
        @((Board).jobs | ForEach-Object id) | Should -Be @($kept)
        Test-Path (JobDir $done) | Should -BeFalse
        Test-Path (JobDir $gone) | Should -BeFalse
        Test-Path (JobDir $kept) | Should -BeTrue
        Events | Where-Object { $_.kind -eq 'note' -and $_.detail -match 'purged' } | Should -Not -BeNullOrEmpty
    }

    It 'removes an idea only when it is done or failed and all its jobs are terminal' {
        $a = F new-idea 'all discarded' -Target $script:Repo
        $ja = F add-spec $a (New-Spec 'A')
        F discard $ja | Out-Null
        Evaluate $ja
        F idea-done $a | Out-Null
        $b = F new-idea 'one still in backlog' -Target $script:Repo
        F add-spec $b (New-Spec 'B') | Out-Null
        F idea-done $b | Out-Null
        $c = F new-idea 'rejected' -Target $script:Repo
        F idea-done $c -Failed -Reason 'rejected in session' | Out-Null
        $d = F new-idea 'still drafting' -Target $script:Repo
        $new = "test/target-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $w, $boot = (F new-idea 'waiting' -Target $new -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false}') -split ' '
        F discard $boot | Out-Null
        Evaluate $boot

        $out = F purge
        $LASTEXITCODE | Should -Be 0
        $out | Should -Contain $a
        $out | Should -Contain $c
        @((Board).ideas | ForEach-Object id) | Should -Be @($b, $d, $w)
        Test-Path (Join-Path $env:FACTORY_HOME "ideas/$a") | Should -BeFalse
        Test-Path (Join-Path $env:FACTORY_HOME "ideas/$b") | Should -BeTrue
    }

    It 'removes a work dir that merge or discard could not delete' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        F set-stage $id build -By runner | Out-Null
        F set-stage $id ready -Reason escalated -Detail 'x' | Out-Null
        $wt = Work $id
        $lock = [IO.File]::Open((Join-Path $wt 'feature.txt'), 'Open', 'Read', 'None')
        try { F discard $id | Out-Null } finally { $lock.Dispose() }
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'discarded'
        Events | Where-Object { $_.kind -eq 'note' -and $_.detail -match 'work dir' } | Should -Not -BeNullOrEmpty
        Evaluate $id
        F purge | Out-Null
        Test-Path $wt | Should -BeFalse
    }

    It 'keeps a finished job with no evaluation, and its folder and idea, until it is evaluated' {
        $i = F new-idea 'unjudged' -Target $script:Repo
        $id = F add-spec $i (New-Spec 'U')
        F discard $id | Out-Null
        F idea-done $i | Out-Null

        $out = F purge
        $LASTEXITCODE | Should -Be 0
        $out | Should -BeNullOrEmpty
        (Job $id).stage | Should -Be 'discarded'
        Test-Path (JobDir $id) | Should -BeTrue
        @((Board).ideas | ForEach-Object id) | Should -Contain $i

        Evaluate $id
        $out = F purge
        $out | Should -Contain $id
        $out | Should -Contain $i
        Job $id | Should -BeNullOrEmpty
    }

    It 'never reuses a purged id' {
        $a = F new-idea 'a' -Target $script:Repo
        $ja = F add-spec $a (New-Spec 'A')
        F discard $ja | Out-Null
        Evaluate $ja
        F idea-done $a | Out-Null
        F purge | Out-Null
        $i = F new-idea 'b' -Target $script:Repo
        $i | Should -Be 'I-002'
        F add-spec $i (New-Spec 'B') | Should -Be 'J-002'
    }

    It 'never reuses a purged id from a board written before seq existed' {
        $a = F new-idea 'a' -Target $script:Repo
        $ja = F add-spec $a (New-Spec 'A')
        F discard $ja | Out-Null
        Evaluate $ja
        F idea-done $a | Out-Null
        $path = Join-Path $env:FACTORY_HOME 'board.json'
        $b = Board; $b.Remove('seq'); Set-Content $path ($b | ConvertTo-Json -Depth 20)
        F purge | Out-Null
        F new-idea 'b' -Target $script:Repo | Should -Be 'I-002'
    }
}

Describe 'bookkeeping' {
    It 'set-cost accumulates and appends a cost event' {
        $id = New-ApprovedJob
        F set-cost $id 1.5 1000 | Out-Null
        F set-cost $id 0.25 500 | Out-Null
        (Job $id).costUsd | Should -Be 1.75
        (Job $id).durationMs | Should -Be 1500
        @(Events | Where-Object kind -eq 'cost').Count | Should -Be 2
    }

    It 'every transition appends a stage event with by' {
        $id = New-ApprovedJob
        Move-ToGate $id
        $stages = @(Events | Where-Object { $_.kind -eq 'stage' -and $_.job -eq $id })
        ($stages | ForEach-Object { "$($_.from)->$($_.to)" }) -join ',' |
            Should -Be '->backlog,backlog->approved,approved->build,build->review,review->approve,approve->gate'
        $stages[1].by | Should -Be 'human'
        $stages[2].by | Should -Be 'runner'
        (Job $id).history.Count | Should -Be 5
    }

    It 'event appends agent-start with the job round' {
        $id = New-ApprovedJob
        F event -Job $id -Kind agent-start -Agent builder | Out-Null
        $e = Events | Where-Object kind -eq 'agent-start'
        $e.agent | Should -Be 'builder'
        $e.round | Should -Be 1
    }

    It 'checkout clones the target into <work>/jobs/<id> on factory/<id> from main, one dir per job, and reuses it' {
        $a = New-ApprovedJob 'A'
        $wa = F checkout $a
        $LASTEXITCODE | Should -Be 0
        $wa | Should -Be (Work $a)
        git -C $wa branch --show-current | Should -Be "factory/$a"
        git -C $wa rev-parse HEAD | Should -Be (RGit rev-parse main)
        Set-Content (Join-Path $wa 'wip.txt') 'kept'
        F checkout $a | Should -Be $wa
        Test-Path (Join-Path $wa 'wip.txt') | Should -BeTrue
        $b = New-ApprovedJob 'B'
        $wb = F checkout $b
        $wb | Should -Be (Work $b)
        git -C $wb branch --show-current | Should -Be "factory/$b"
        RGit branch --list 'factory/*' | Should -BeNullOrEmpty    # nothing is pushed by checkout
    }

    It 'checkout fetches and points factory/base at the base branch as it is now, so the builder can merge it' {
        # J-022: J-021 merged into main after J-022's branch was cut, and nothing brought it in.
        $a = New-ApprovedJob 'A'
        $wa = F checkout $a
        $seed = Join-Path $TestDrive "seed-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        git clone -q (Bare) $seed 2>&1 | Out-Null
        Set-Content (Join-Path $seed 'later.txt') 'merged after the branch was cut'
        git -C $seed add -A 2>&1 | Out-Null
        git -C $seed -c user.email=f@t -c user.name=f commit -q -m 'later on main' 2>&1 | Out-Null
        git -C $seed push -q origin main 2>&1 | Out-Null

        F checkout $a | Should -Be $wa
        git -C $wa rev-parse factory/base | Should -Be (RGit rev-parse main)
        git -C $wa branch --show-current | Should -Be "factory/$a"
    }

    It 'status -Json prints the board' {
        New-BacklogJob | Out-Null
        (F status -Json | ConvertFrom-Json).jobs[0].id | Should -Be 'J-001'
    }
}

Describe 'evaluations' {
    It 'refuses a job that is not finished, or a file with no Rating line, and writes nothing' {
        $id = New-ApprovedJob
        F eval $id -File (Eval-File) | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        Move-ToReview $id
        F set-stage $id ready -Reason escalated | Out-Null
        foreach ($bad in 'Rating: great', 'rating: good', '# Eval: no rating') {
            F eval $id -File (Eval-File $bad) | Out-Null
            $LASTEXITCODE | Should -Be 2
        }
        Test-Path (Join-Path $env:FACTORY_HOME 'evals') | Should -BeFalse
        @(Events | Where-Object kind -eq 'eval').Count | Should -Be 0
    }

    It 'stores the markdown and a snapshot of the board facts, appends an eval event, and replaces both on re-eval' {
        $id = New-ApprovedJob 'Evaluated'
        Commit-OnBranch $id
        Move-ToGate $id
        F set-cost $id 1.25 60000 | Out-Null
        F gate $id | Out-Null
        F eval $id -File (Eval-File 'Rating: mixed') | Out-Null
        $LASTEXITCODE | Should -Be 0

        $dir = Join-Path $env:FACTORY_HOME 'evals'
        Get-Content (Join-Path $dir "$id.md") -TotalCount 1 | Should -Be 'Rating: mixed'
        $e = Get-Content (Join-Path $dir "$id.json") -Raw | ConvertFrom-Json -AsHashtable -DateKind String
        $e.job | Should -Be $id
        $e.title | Should -Be 'Evaluated'
        $e.rating | Should -Be 'mixed'
        $e.stage | Should -Be 'ready'
        $e.round | Should -Be 1
        $e.flag | Should -BeNullOrEmpty
        $e.reviews.code | Should -Be 'PASS'
        $e.decision | Should -Be 'APPROVE'
        $e.gate | Should -Be 'green'
        $e.merged | Should -BeNullOrEmpty
        $e.costUsd | Should -Be 1.25
        $e.durationMs | Should -Be 60000
        $e.target | Should -Be $script:Repo
        $e.evaluatedAt | Should -Not -BeNullOrEmpty
        (Events | Where-Object kind -eq 'eval' | Select-Object -Last 1).detail | Should -Be 'mixed'

        F merge $id | Out-Null
        F eval $id -File (Eval-File 'Rating: good') | Out-Null
        $e = Get-Content (Join-Path $dir "$id.json") -Raw | ConvertFrom-Json -AsHashtable -DateKind String
        $e.rating | Should -Be 'good'
        $e.stage | Should -Be 'done'
        $e.merged | Should -Be (Job $id).merged.sha
        Get-Content (Join-Path $dir "$id.md") -TotalCount 1 | Should -Be 'Rating: good'
    }

    It 'snapshots cost by model from the run logs, time per agent from the events, and the gate time' {
        $id = New-ApprovedJob 'Timed'
        Move-ToReview $id
        F set-stage $id ready -Reason escalated | Out-Null
        $at = { param($s) "2026-01-01T00:$s.000Z" }
        foreach ($e in @(
            @{ kind = 'agent-start'; agent = 'builder'; ts = (& $at '00:00') }, @{ kind = 'agent-stop'; agent = 'builder'; ts = (& $at '10:00') },
            @{ kind = 'agent-start'; agent = 'code-reviewer'; ts = (& $at '10:00') }, @{ kind = 'agent-stop'; agent = 'code-reviewer'; ts = (& $at '11:30') },
            @{ kind = 'stage'; from = 'approve'; to = 'gate'; ts = (& $at '12:00') }, @{ kind = 'gate'; detail = 'build=0'; ts = (& $at '12:40') })) {
            $e.job = $id; $e.round = 1
            F event ($e | ConvertTo-Json -Compress) | Out-Null
        }
        $usage = { param($usd, $out) @{ 'claude-opus-5-5' = @{ costUSD = $usd; outputTokens = $out }; 'claude-sonnet-5-5' = @{ costUSD = 0.25; outputTokens = 10 } } }
        Set-Content (Join-Path (JobDir $id) 'run-1.jsonl') @('not json', (@{ type = 'result'; total_cost_usd = 2.25; modelUsage = (& $usage 2.0 100) } | ConvertTo-Json -Compress -Depth 5))
        # A session that a background task wakes again ends each turn with a result carrying the
        # session's cumulative usage so far (J-019's run-8 had five); only the last one counts.
        Set-Content (Join-Path (JobDir $id) 'run-2.jsonl') @(
            (@{ type = 'result'; total_cost_usd = 0.75; modelUsage = (& $usage 0.5 20) } | ConvertTo-Json -Compress -Depth 5),
            (@{ type = 'result'; total_cost_usd = 1.25; modelUsage = (& $usage 1.0 50) } | ConvertTo-Json -Compress -Depth 5))

        F eval $id -File (Eval-File) | Out-Null
        $e = Get-Content (Join-Path $env:FACTORY_HOME "evals/$id.json") -Raw | ConvertFrom-Json -AsHashtable -DateKind String
        $e.models['claude-opus-5-5'].costUsd | Should -Be 3.0
        $e.models['claude-opus-5-5'].outputTokens | Should -Be 150
        $e.models['claude-sonnet-5-5'].costUsd | Should -Be 0.5
        @($e.agents | ForEach-Object agent) | Should -Be @('builder', 'code-reviewer')
        $e.agents[0].ms | Should -Be 600000
        $e.agents[1].ms | Should -Be 90000
        $e.agents[0].round | Should -Be 1
        $e.gateMs | Should -Be 40000
    }

    It 'records human feedback on an evaluation, refuses one without an evaluation or a Rating line, and keeps it through a re-eval' {
        $id = New-BacklogJob 'Felt'
        F discard $id | Out-Null
        F feedback $id -File (Eval-File 'Rating: good') | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        F eval $id -File (Eval-File 'Rating: mixed') | Out-Null
        F feedback $id -File (Eval-File '# no rating') | Out-Null
        $LASTEXITCODE | Should -Be 2
        Test-Path (Join-Path $env:FACTORY_HOME "evals/$id.feedback.md") | Should -BeFalse

        F feedback $id -File (Eval-File 'Rating: poor') | Out-Null
        $LASTEXITCODE | Should -Be 0
        $dir = Join-Path $env:FACTORY_HOME 'evals'
        Get-Content (Join-Path $dir "$id.feedback.md") -TotalCount 1 | Should -Be 'Rating: poor'
        $e = Get-Content (Join-Path $dir "$id.json") -Raw | ConvertFrom-Json -AsHashtable -DateKind String
        $e.rating | Should -Be 'mixed'
        $e.human.rating | Should -Be 'poor'
        $e.human.at | Should -Not -BeNullOrEmpty
        (Events | Select-Object -Last 1).kind | Should -Be 'feedback'
        (Events | Select-Object -Last 1).detail | Should -Be 'poor'

        F eval $id -File (Eval-File 'Rating: good') | Out-Null
        $e = Get-Content (Join-Path $dir "$id.json") -Raw | ConvertFrom-Json -AsHashtable -DateKind String
        $e.rating | Should -Be 'good'
        $e.human.rating | Should -Be 'poor'

        F purge | Out-Null
        F feedback $id -File (Eval-File 'Rating: mixed') | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Get-Content (Join-Path $dir "$id.json") -Raw | ConvertFrom-Json).human.rating | Should -Be 'mixed'
    }

    It 'purge removes the job but keeps its evaluation' {
        $id = New-BacklogJob 'Gone'
        F discard $id | Out-Null
        F eval $id -File (Eval-File 'Rating: poor') | Out-Null
        F purge | Out-Null
        Job $id | Should -BeNullOrEmpty
        Test-Path (Join-Path $env:FACTORY_HOME "evals/$id.md") | Should -BeTrue
        Test-Path (Join-Path $env:FACTORY_HOME "evals/$id.json") | Should -BeTrue
    }
}

Describe 'targets' {
    BeforeEach {
        $script:New = "test/target-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    }

    It 'new-idea refuses a missing target, a local path or anything not owner/name, and leaves the board empty' -ForEach @(
        @{ t = $null }, @{ t = 'D:\code\Scratchpad\todo' }, @{ t = 'Scratchpad\todo' }, @{ t = 'todo' }, @{ t = 'a/b/c' }, @{ t = 'https://github.com/a/b' }
    ) {
        if ($t) { F new-idea 'x' -Target $t | Out-Null } else { F new-idea 'x' | Out-Null }
        $LASTEXITCODE | Should -Not -Be 0
        Test-Path (Join-Path $env:FACTORY_HOME 'board.json') | Should -BeFalse
    }

    It 'check-target asks GitHub: existing, missing or empty (no main) is new; refuses a non-target; writes no board' {
        F check-target $script:Repo | Should -Be 'existing'
        F check-target $script:New | Should -Be 'new'
        git init -q --bare -b main (Bare $script:New) 2>&1 | Out-Null
        F check-target $script:New | Should -Be 'new'
        F check-target 'D:\code\todo' | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        Test-Path (Join-Path $env:FACTORY_HOME 'board.json') | Should -BeFalse
        (GhRequests $script:New | Select-Object -Last 1).auth | Should -Be "Bearer $script:Token"
    }

    It 'check-target and new-idea refuse when GitHub refuses the token' {
        $bad = Join-Path $TestDrive 'bad.env'
        Set-Content $bad @('GITHUB_TOKEN=', "GITHUB_API_URL=http://localhost:$script:GhPort", "FACTORY_GIT_BASE=$script:GitUrl")
        $env:FACTORY_ENV_FILE = $bad
        F check-target $script:Repo | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        F list-repos | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }

    It 'list-repos prints the token user''s repos as a JSON array, newest push first; writes no board' {
        $older = "test/older-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Remote $older
        (Get-Item (Bare $older)).LastWriteTime = (Get-Date).AddDays(-30)
        New-Remote $script:New
        $out = F list-repos
        $LASTEXITCODE | Should -Be 0
        $repos = @($out | ConvertFrom-Json)
        $repos | Should -Contain $script:New
        $repos | Should -Contain $older
        [array]::IndexOf($repos, $script:New) | Should -BeLessThan ([array]::IndexOf($repos, $older))
        Test-Path (Join-Path $env:FACTORY_HOME 'board.json') | Should -BeFalse
    }

    It 'clone-target replaces a folder with a shallow clone of main' {
        $dir = Join-Path $TestDrive "clone-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory $dir | Out-Null
        Set-Content (Join-Path $dir 'stale.txt') 'old'
        F clone-target $script:Repo $dir | Out-Null
        $LASTEXITCODE | Should -Be 0
        Test-Path (Join-Path $dir 'stale.txt') | Should -BeFalse
        Get-Content (Join-Path $dir 'README.md') | Should -Be 'fixture'
        git -C $dir rev-parse --is-shallow-repository | Should -Be 'true'
        Get-Content (Join-Path $dir '.git/config') -Raw | Should -Not -Match $script:Token
    }

    It 'new-idea on an existing repo stores the target and goes straight to drafting' {
        $idea = F new-idea 'a filter' -Target $script:Repo
        $LASTEXITCODE | Should -Be 0
        $i = (Board).ideas | Where-Object id -eq $idea
        $i.target | Should -Be $script:Repo
        $i.status | Should -Be 'drafting'
        @((Board).jobs).Count | Should -Be 0
        Get-Content (Join-Path $env:FACTORY_HOME "ideas/$idea/idea.md") | Select-Object -Last 1 | Should -Be "Target: $script:Repo"
    }

    It 'new-idea refuses a new target without valid scaffold options, and scaffold options for an existing repo' -ForEach @(
        @{ case = 'new, none'; target = 'NEW'; scaffold = $null }
        @{ case = 'bad name'; target = 'NEW'; scaffold = '{"name":"Todo-App","resource":"Todo","db":"sqlite","ui":"react","events":false}' }
        @{ case = 'bad resource'; target = 'NEW'; scaffold = '{"name":"todo","resource":"todo","db":"sqlite","ui":"react","events":false}' }
        @{ case = 'bad db'; target = 'NEW'; scaffold = '{"name":"todo","resource":"Todo","db":"mysql","ui":"react","events":false}' }
        @{ case = 'bad ui'; target = 'NEW'; scaffold = '{"name":"todo","resource":"Todo","db":"sqlite","ui":"vue","events":false}' }
        @{ case = 'auth, no web ui'; target = 'NEW'; scaffold = '{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false,"auth":true}' }
        @{ case = 'bad auth'; target = 'NEW'; scaffold = '{"name":"todo","resource":"Todo","db":"sqlite","ui":"react","events":false,"auth":"yes"}' }
        @{ case = 'existing'; target = 'REPO'; scaffold = '{"name":"todo","resource":"Todo","db":"sqlite","ui":"react","events":false}' }
    ) {
        $t = if ($target -eq 'NEW') { $script:New } else { $script:Repo }
        if ($scaffold) { F new-idea $case -Target $t -Scaffold $scaffold | Out-Null } else { F new-idea $case -Target $t | Out-Null }
        $LASTEXITCODE | Should -Not -Be 0
        Test-Path (Join-Path $env:FACTORY_HOME 'board.json') | Should -BeFalse
    }

    It 'new-idea on a new target with scaffold options waits, and creates a bootstrap job in backlog' {
        $out = F new-idea 'a todo app' -Target $script:New -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"react","events":false}'
        $LASTEXITCODE | Should -Be 0
        $out | Should -Be 'I-001 J-001'
        $i = (Board).ideas[0]
        $i.status | Should -Be 'waiting'
        $i.scaffold.name | Should -Be 'todo'
        $i.jobs | Should -Be @('J-001')
        $j = Job 'J-001'
        $j.kind | Should -Be 'bootstrap'
        $j.stage | Should -Be 'backlog'
        $j.target | Should -Be $script:New
        $spec = Get-Content (Join-Path (JobDir 'J-001') 'spec.md') -Raw
        $spec | Should -Match '# Spec: J-001 — Bootstrap todo from template-projects'
        $spec | Should -Match ([regex]::Escape("- Repo: $script:New"))
        $spec | Should -Match ([regex]::Escape('dotnet new aspire-base -n todo -o . --dbProvider sqlite --resource Todo'))
        $spec | Should -Match ([regex]::Escape('`aspire-addon add ui-react`'))
        $spec | Should -Not -Match 'dotnet new aspire-ui|aspire-events|add events'
        $spec | Should -Match ([regex]::Escape('/aspire-starter:boot-strap-app'))
        $spec | Should -Match 'Skip Phase 5'
        $spec | Should -Not -Match 'scaffold-aspire-app'
        $spec | Should -Match ([regex]::Escape('- build: `dotnet build todo.slnx`'))
        $spec | Should -Match ([regex]::Escape('- test: `dotnet test todo.slnx`'))
        $spec | Should -Match ([regex]::Escape('- apphost: `dotnet build .aspire/apphost.cs`'))
        $spec | Should -Match ([regex]::Escape('- ui: `Set-Location src/ui/react; if (-not (Test-Path node_modules/.package-lock.json) -or (Get-Item package-lock.json).LastWriteTime -gt (Get-Item node_modules/.package-lock.json).LastWriteTime) { npm ci }; npm run build`'))
        $spec | Should -Not -Match '\{\{'
        F approve 'J-001' | Out-Null
        $LASTEXITCODE | Should -Be 0
        Test-Path (Bare $script:New) | Should -BeFalse    # nothing is created on GitHub before checkout
    }

    It 'a bootstrap with auth adds sign-in after the front end, and every bootstrap gates on infra check' {
        F new-idea 'a todo app' -Target $script:New -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"blazor","events":false,"auth":true}' | Out-Null
        $spec = Get-Content (Join-Path (JobDir 'J-001') 'spec.md') -Raw
        $spec | Should -Match ([regex]::Escape('`aspire-addon add ui-blazor --web true --maui false`'))
        $spec | Should -Match ([regex]::Escape('`aspire-addon add auth`'))
        $spec.IndexOf('add auth') | Should -BeGreaterThan $spec.IndexOf('add ui-blazor')
        $spec | Should -Match ([regex]::Escape('- infra: `aspire-addon infra check`'))
        (Board).ideas[0].scaffold.auth | Should -BeTrue
    }

    It "the bootstrap spec's ui gate runs npm ci only when node_modules is missing or older than package-lock.json" {
        F new-idea 'x' -Target $script:New -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"react","events":false}' | Out-Null
        $cmd = (Get-Content (Join-Path (JobDir 'J-001') 'spec.md') | Where-Object { $_ -like '- ui: *' }) -replace '^- ui: `(.*)`$', '$1'
        $root = Join-Path $TestDrive 'ui-guard'
        $ui = New-Item -ItemType Directory -Force (Join-Path $root 'src/ui/react')
        Set-Content (Join-Path $ui 'package-lock.json') '{}'
        $calls = Join-Path $TestDrive 'npm-calls.txt'
        # A fake npm: records its arguments, and `ci` writes the hidden lockfile as npm does.
        $fake = "function npm { Add-Content '$calls' (`$args -join ' '); if (`$args[0] -eq 'ci') { New-Item -ItemType Directory -Force node_modules | Out-Null; Set-Content node_modules/.package-lock.json '{}' } }"
        function Run-Ui { Push-Location $root; try { & pwsh -NoProfile -Command "$fake; $cmd" | Out-Null } finally { Pop-Location } }

        Run-Ui
        Run-Ui
        (Get-Item (Join-Path $ui 'package-lock.json')).LastWriteTime = (Get-Date).AddMinutes(1)
        Run-Ui
        Get-Content $calls | Should -Be @('ci', 'run build', 'run build', 'ci', 'run build')
    }

    It 'the bootstrap spec maps blazor, postgres and events to their aspire-addon commands' {
        F new-idea 'x' -Target $script:New -Scaffold '{"name":"shop","resource":"Product","db":"postgres","ui":"blazor-maui","events":true}' | Out-Null
        $spec = Get-Content (Join-Path (JobDir 'J-001') 'spec.md') -Raw
        $spec | Should -Match ([regex]::Escape('dotnet new aspire-base -n shop -o . --dbProvider postgres --resource Product'))
        $spec | Should -Match ([regex]::Escape('`aspire-addon add ui-blazor --web true --maui true`'))
        $spec | Should -Match ([regex]::Escape('`aspire-addon add events`'))
        $spec | Should -Not -Match 'dotnet new aspire-(ui|events)|ui-react'
        $spec | Should -Not -Match '- ui:'
    }

    It 'merging a bootstrap job releases its idea to drafting and prints released <idea>' {
        F new-idea 'a todo app' -Target $script:New -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false}' | Out-Null
        F approve 'J-001' | Out-Null
        $wt = F checkout 'J-001'
        Set-Content (Join-Path $wt 'todo.slnx') '<Solution />'
        git -C $wt add -A 2>&1 | Out-Null
        git -C $wt -c user.email=f@t -c user.name=f commit -q -m 'scaffold' 2>&1 | Out-Null
        Move-ToGate 'J-001'
        F gate 'J-001' | Out-Null
        $out = F merge 'J-001'
        $LASTEXITCODE | Should -Be 0
        $out | Should -Contain 'released I-001'
        (Board).ideas[0].status | Should -Be 'drafting'
    }

    It 'a bootstrap PR merged on GitHub releases its idea too, through sync-prs' {
        F new-idea 'a todo app' -Target $script:New -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false}' | Out-Null
        F approve 'J-001' | Out-Null
        $wt = F checkout 'J-001'
        Set-Content (Join-Path $wt 'todo.slnx') '<Solution />'
        git -C $wt add -A 2>&1 | Out-Null
        git -C $wt -c user.email=f@t -c user.name=f commit -q -m 'scaffold' 2>&1 | Out-Null
        Move-ToGate 'J-001'
        F gate 'J-001' | Out-Null
        F publish 'J-001' | Out-Null
        Invoke-RestMethod -Method Put "http://localhost:$script:GhPort/repos/$script:New/pulls/$((Job 'J-001').pr.number)/merge" `
            -Headers @{ Authorization = "Bearer $script:Token" } -ContentType 'application/json' -Body '{}' | Out-Null
        $out = F sync-prs
        $LASTEXITCODE | Should -Be 0
        $out | Should -Contain 'J-001'
        $out | Should -Contain 'released I-001'
        (Job 'J-001').stage | Should -Be 'done'
        (Board).ideas[0].status | Should -Be 'drafting'
    }

    It 'approve refuses a spec with no Target, a local path Repo, or no Gate' -ForEach @(
        @{ case = 'no target'; repo = $null; gate = @('build: `exit 0`') }
        @{ case = 'local path'; repo = 'D:\code\Scratchpad\todo'; gate = @('build: `exit 0`') }
        @{ case = 'no gate'; repo = 'NEW'; gate = @() }
    ) {
        $r = if ($repo -eq 'NEW') { $script:New } else { $repo }
        $idea = F new-idea 'x' -Target $script:Repo
        $id = F add-spec $idea (New-Spec $case $r $gate)
        F approve $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'backlog'
    }

    It 'add-spec, edit-spec and approve take job.target from the spec' {
        $id = New-BacklogJob 'T' $script:Repo
        (Job $id).target | Should -Be $script:Repo
        F edit-spec $id -File (New-Spec 'T' $script:New) | Out-Null
        (Job $id).target | Should -Be $script:New
        F approve $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).target | Should -Be $script:New
    }

    It "checkout creates a missing repo on GitHub, private, with main's one empty commit, and clones it" {
        $id = New-ApprovedJob 'New app' $script:New
        $wt = F checkout $id
        $LASTEXITCODE | Should -Be 0
        $wt | Should -Be (Work $id)
        $create = GhRequests $script:New | Where-Object { $_.method -eq 'POST' -and $_.path -eq '/user/repos' }
        $create.body.name | Should -Be ($script:New -split '/')[1]
        $create.body.private | Should -BeTrue
        @(git -C (Bare $script:New) log --oneline main).Count | Should -Be 1
        git -C $wt branch --show-current | Should -Be "factory/$id"
    }

    It 'checkout creates a repo for another owner in that organization' {
        $org = "acme/app-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $id = New-ApprovedJob 'Org app' $org
        F checkout $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (GhRequests $org | Where-Object { $_.method -eq 'POST' -and $_.path -eq '/orgs/acme/repos' }).body.private | Should -BeTrue
    }

    It 'checkout pushes an empty initial commit to an existing repo with no main' {
        git init -q --bare -b main (Bare $script:New) 2>&1 | Out-Null
        $id = New-ApprovedJob 'Empty repo' $script:New
        F checkout $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        @(git -C (Bare $script:New) log --oneline main).Count | Should -Be 1
        GhRequests $script:New | Where-Object { $_.method -eq 'POST' -and $_.path -match '/repos$' } | Should -BeNullOrEmpty
    }

    It "the gate runs the spec's Gate commands in the job's work dir" {
        Remove-Item Env:FACTORY_GATE_CMDS
        $id = New-ApprovedJob 'Gated' $script:Repo @('smoke: `Set-Content gate-ran.txt ok`', 'test: `exit 3`')
        F checkout $id | Out-Null
        Move-ToGate $id
        F gate $id | Out-Null
        $g = (Job $id).gate
        $g.smoke | Should -Be 0
        $g.test | Should -Be 3
        Test-Path (Join-Path (Work $id) 'gate-ran.txt') | Should -BeTrue
        (Job $id).flag.reason | Should -Be 'gate-failed'
    }

    It 'merge records the repo-relative added and modified paths' {
        $id = New-ApprovedJob 'Ship'
        $wt = F checkout $id
        New-Item -ItemType Directory (Join-Path $wt 'src') | Out-Null
        Set-Content (Join-Path $wt 'src/app.txt') 'app'
        Set-Content (Join-Path $wt 'README.md') 'changed'
        git -C $wt add -A 2>&1 | Out-Null
        git -C $wt -c user.email=f@t -c user.name=f commit -q -m 'ship' 2>&1 | Out-Null
        Move-ToGate $id
        F gate $id | Out-Null
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        $j = Job $id
        $j.merged.added | Should -Be @('src/app.txt')
        $j.merged.modified | Should -Be @('README.md')
    }
}

Describe 'deploy' {
    BeforeAll {
        # A job in deploy, its branch carrying one commit beyond main.
        function Move-ToDeploy([string]$id) {
            Move-ToReview $id
            Commit-OnBranch $id
            Write-Reviews $id (All 'Verdict: PASS')
            F read-reviews $id | Out-Null
        }

        # A commit on the job branch in its work dir: adds $file with $text, or removes it.
        function Commit-Change([string]$id, [string]$file, [string]$text, [switch]$Remove) {
            $wt = Work $id
            if ($Remove) { Remove-Item (Join-Path $wt $file) } else { Set-Content (Join-Path $wt $file) $text }
            git -C $wt add -A 2>&1 | Out-Null
            git -C $wt -c user.email=f@t -c user.name=f commit -q -m "change $file" 2>&1 | Out-Null
        }

        function RepoName { ($script:Repo -split '/')[1] }

        # Polls the board until the job's preview reaches $status (a detached destroy), or fails.
        function Wait-Preview([string]$id, [string]$status) {
            $deadline = (Get-Date).AddSeconds(30)
            while ((Job $id).deploy.status -ne $status) {
                if ((Get-Date) -gt $deadline) { throw "preview of $id never reached $status (is $((Job $id).deploy.status))" }
                Start-Sleep -Milliseconds 300
            }
        }
    }

    BeforeEach { Use-Deploys }
    AfterEach { Clear-Deploys }

    Context 'HTTP smoke' {
        AfterEach { Remove-Item (Join-Path $script:GitBase "$script:Repo.unhealthy") -ErrorAction SilentlyContinue }

        It 'a /health that never answers 200 is a failed attempt: it says so, skips the sign-in, and the job stays in deploy' {
            $env:FACTORY_SIGNIN_LOG = Join-Path $TestDrive "signin-$([guid]::NewGuid().ToString('N').Substring(0,8)).jsonl"
            $id = New-ApprovedJob
            Move-ToDeploy $id
            Set-Content (Join-Path $script:GitBase "$script:Repo.unhealthy") ''
            F deploy $id | Out-Null
            $LASTEXITCODE | Should -Be 1
            $j = Job $id
            $j.stage | Should -Be 'deploy'
            $j.deploy.status | Should -Be 'failed'
            Get-Content (Join-Path (JobDir $id) 'deploy-1.log') -Raw | Should -Match '## Smoke[\s\S]*answered 503'
            (Events | Where-Object { $_.kind -eq 'deploy' -and $_.job -eq $id }).detail | Should -Match 'failed \(attempt 1\): Error: smoke: .*/web/api/health answered 503'
            Test-Path $env:FACTORY_SIGNIN_LOG | Should -BeFalse
        }

        It 'a pass leaves its results in smoke-N.log, and the job goes on to approve' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Job $id).stage | Should -Be 'approve'
            Get-Content (Join-Path (JobDir $id) 'smoke-1.log') -Raw | Should -Match '/web/api/health answered 200'
        }
    }

    Context 'sign-in smoke' {
        BeforeEach {
            $env:FACTORY_SIGNIN_LOG = Join-Path $TestDrive "signin-$([guid]::NewGuid().ToString('N').Substring(0,8)).jsonl"
            function SigninCalls { if (Test-Path $env:FACTORY_SIGNIN_LOG) { Get-Content $env:FACTORY_SIGNIN_LOG | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } } }
        }

        It 'signs in as the QA user on the front end, the password on stdin only, and the job goes on to approve' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Job $id).stage | Should -Be 'approve'
            $env_ = (Job $id).deploy.env
            $calls = @(SigninCalls)
            $calls.Count | Should -Be 1
            $a = $calls[0].args -join ' '
            $a | Should -Match "--url http\S*/preview/$script:Repo/$env_/web"
            $a | Should -Match ([regex]::Escape("--user qa-$env_@$(RepoName).test"))
            $a | Should -Not -Match "pw-$env_"
            $calls[0].stdin | Should -Be "pw-$env_"
        }

        It 'a passed sign-in leaves its steps in signin/smoke-N.log, never the password' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            $f = Join-Path (JobDir $id) 'signin/smoke-1.log'
            Test-Path $f | Should -BeTrue
            $log = Get-Content $f -Raw
            $log | Should -Match 'signed in: Sign out is showing'
            $log | Should -Match '/api/me answered 200'
            $log | Should -Not -Match "pw-$((Job $id).deploy.env)"
        }

        It 'a failed sign-in is a failed attempt: the log ends with the smoke, never the password, and the job stays in deploy' {
            $env:FACTORY_SIGNIN_EXIT = '1'
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
            $j = Job $id
            $j.stage | Should -Be 'deploy'
            $j.deploy.status | Should -Be 'failed'
            $log = Get-Content (Join-Path (JobDir $id) 'deploy-1.log') -Raw
            $log | Should -Match '## Sign-in smoke'
            $log | Should -Match 'AADSTS500011'
            $log | Should -Not -Match "pw-$($j.deploy.env)"
            (Events | Where-Object { $_.kind -eq 'deploy' -and $_.job -eq $id }).detail | Should -Match 'AADSTS500011'
            @(SigninCalls).Count | Should -Be 1
        }

        It 'retries AADSTS9002326 while the new Entra app replicates, and the job goes on to approve' {
            # J-025: the smoke failed on it, and passed about 90 s later.
            $env:FACTORY_SIGNIN_LAG = '2'
            $env:FACTORY_SIGNIN_RETRY_SECONDS = '0'
            try {
                $id = New-ApprovedJob
                Move-ToDeploy $id
                F deploy $id | Out-Null
                $LASTEXITCODE | Should -Be 0
                @(SigninCalls).Count | Should -Be 3
                (Job $id).stage | Should -Be 'approve'
            } finally { Remove-Item Env:FACTORY_SIGNIN_LAG, Env:FACTORY_SIGNIN_RETRY_SECONDS }
        }

        It 'gives up on AADSTS9002326 after five tries, as a failed attempt' {
            $env:FACTORY_SIGNIN_LAG = '99'
            $env:FACTORY_SIGNIN_RETRY_SECONDS = '0'
            try {
                $id = New-ApprovedJob
                Move-ToDeploy $id
                F deploy $id | Out-Null
                $LASTEXITCODE | Should -Not -Be 0
                @(SigninCalls).Count | Should -Be 5
                (Job $id).deploy.status | Should -Be 'failed'
            } finally { Remove-Item Env:FACTORY_SIGNIN_LAG, Env:FACTORY_SIGNIN_RETRY_SECONDS }
        }

        It 'is skipped when the preview has no QA user' {
            Set-Content (Join-Path $script:GitBase "$script:Repo.noauth") ''
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Job $id).stage | Should -Be 'approve'
            @(SigninCalls).Count | Should -Be 0
        }
    }

    It 'with no HCP_ORG, all-PASS reviews go straight to approve and a note says deploys are off' {
        $env:FACTORY_ENV_FILE = $script:EnvFile
        $id = New-ApprovedJob
        Move-ToReview $id
        Write-Reviews $id (All 'Verdict: PASS')

        F read-reviews $id | Should -Be 'approve'
        (Events | Where-Object { $_.kind -eq 'note' -and $_.job -eq $id }).detail | Should -Contain 'deploy skipped: no HCP_ORG in .env'
    }

    It 'with HCP_ORG, an all-PASS bootstrap skips deploy: its deploy.yml reaches main only when it merges' {
        $out = F new-idea 'boot' -Target "test/boot-$([guid]::NewGuid().ToString('N').Substring(0,8))" -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false}'
        $id = ($out -split ' ')[1]
        F approve $id | Out-Null
        Move-ToReview $id
        Write-Reviews $id (All 'Verdict: PASS')

        F read-reviews $id | Should -Be 'approve'
        @((Events | Where-Object { $_.kind -eq 'note' -and $_.job -eq $id }).detail -match 'deploy skipped: bootstrap') | Should -HaveCount 1
    }

    It 'deploy on a bootstrap already in deploy moves it to approve, even with its attempts used up' {
        $out = F new-idea 'boot' -Target "test/boot-$([guid]::NewGuid().ToString('N').Substring(0,8))" -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false}'
        $id = ($out -split ' ')[1]
        # As J-018 was before this rule: in deploy, three attempts spent on GitHub's 404.
        $b = Board; $j = $b.jobs | Where-Object id -eq $id
        $j.stage = 'deploy'; $j.deploy = @{ attempts = 3; status = 'failed' }
        Set-Content (Join-Path $env:FACTORY_HOME 'board.json') ($b | ConvertTo-Json -Depth 20)

        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'approve'
        (Job $id).deploy.status | Should -Be 'down'
        (Events | Where-Object { $_.kind -eq 'note' -and $_.job -eq $id }).detail | Should -Match 'deploy skipped: bootstrap'
    }

    It 'with HCP_ORG, all-PASS reviews move the job to deploy' {
        $id = New-ApprovedJob
        Move-ToReview $id
        Write-Reviews $id (All 'Verdict: PASS')

        F read-reviews $id | Should -Be 'deploy'
    }

    It 'refuses outside the deploy stage' {
        $id = New-ApprovedJob
        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }

    It 'applies the preview and moves the job to approve with its urls' {
        $id = New-ApprovedJob
        Move-ToDeploy $id

        $out = F deploy $id
        $LASTEXITCODE | Should -Be 0
        ($out -join "`n") | Should -Match "frontend: http\S*/preview/$script:Repo/j001/web"

        $j = Job $id
        $j.stage | Should -Be 'approve'
        $j.deploy.status | Should -Be 'up'
        $j.deploy.env | Should -Be 'j001'
        $j.deploy.workspace | Should -Be "$(RepoName)-j001"
        $j.deploy.workspaceId | Should -Be (HcpWorkspace "$(RepoName)-j001").id
        $j.deploy.attempts | Should -Be 1
        $j.deploy.urls.frontend | Should -Match "/preview/$script:Repo/j001/web$"
        $j.deploy.urls.api | Should -Match "/preview/$script:Repo/j001/api$"
        $j.deploy.urls.health | Should -Match "/preview/$script:Repo/j001/web/api/health$"
        $j.deploy.sha | Should -Be (git -C (Work $id) rev-parse HEAD)
        $j.deploy.destroyAt | Should -Not -BeNullOrEmpty
        (Events | Where-Object { $_.kind -eq 'deploy' -and $_.job -eq $id }).detail | Should -Match "^up http.*/preview/$script:Repo/j001/web$"
    }

    It 'prepares HCP: the repo project with the Azure varset, idle dev and staging, and the preview workspace on a 24 hour timer' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        F deploy $id | Out-Null

        $s = DeployState
        $project = $s.projects.Values | Where-Object { $_.name -eq (RepoName) }
        $project | Should -Not -BeNullOrEmpty
        $s.varsets['varset-azure'] | Should -Contain $project.id
        foreach ($e in 'dev', 'staging', 'j001') { (HcpWorkspace "$(RepoName)-$e").project | Should -Be $project.id }
        (HcpWorkspace "$(RepoName)-dev").autoDestroyAt | Should -BeNullOrEmpty
        $at = [datetime]::Parse((HcpWorkspace "$(RepoName)-j001").autoDestroyAt).ToUniversalTime()
        ($at - (Get-Date).ToUniversalTime()).TotalHours | Should -BeGreaterThan 23
        ($at - (Get-Date).ToUniversalTime()).TotalHours | Should -BeLessThan 25
    }

    It 'prepares GitHub and Azure: preview variables, the HCP team token as a secret through gh, and the federated credential' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        F deploy $id | Out-Null

        $vars = (DeployState).vars
        $vars["$script:Repo/env/preview"].AZURE_CLIENT_ID | Should -Be 'gh-app-client'
        $vars["$script:Repo/env/preview"].AZURE_TENANT_ID | Should -Be 'tenant-1'
        $vars["$script:Repo/env/preview"].AZURE_SUBSCRIPTION_ID | Should -Be 'sub-1'
        $vars["$script:Repo/repo"].TF_CLOUD_ORGANIZATION | Should -Be 'test-org'

        $calls = @(ToolCalls)
        $secret = $calls | Where-Object { $_.args[0] -eq 'secret' } | Select-Object -First 1
        $secret.args | Should -Be @('secret', 'set', 'TF_API_TOKEN', '--env', 'preview', '--repo', $script:Repo)
        $secret.stdin | Should -Be 'team-token-for-actions'
        $secret.ghTokenValue | Should -Be $script:Token
        $fed = $calls | Where-Object { $_.args -contains 'create' } | Select-Object -First 1
        ($fed.args -join ' ') | Should -Match 'ad app federated-credential create --id gh-app-client'
        # The JSON goes through a file: az on Windows is a .cmd, and cmd strips the quotes of inline JSON.
        $p = $fed.args[[array]::IndexOf($fed.args, '--parameters') + 1]
        $p | Should -BeLike '@*'
        ($fed.files[0] | ConvertFrom-Json).subject | Should -Be "repo:$($script:Repo):environment:preview"
        ($fed.files[0] | ConvertFrom-Json).issuer | Should -Be 'https://token.actions.githubusercontent.com'
        $env:GH_TOKEN | Should -BeNullOrEmpty
    }

    It 'a repo on the immutable OIDC subject gets a federated credential for its ID-suffixed subject' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        # As kamabery/todoapp-two: new repos default to repo:<owner>@<id>/<name>@<id>, and J-019 failed AADSTS700213.
        $oidc = Join-Path $script:GitBase "$script:Repo.oidc.json"
        Set-Content $oidc '{"use_default":true,"use_immutable_subject":true,"sub_claim_prefix":"repo:test@41/target@42"}'
        try { F deploy $id | Out-Null } finally { Remove-Item $oidc }

        $fed = @(ToolCalls) | Where-Object { $_.args -contains 'create' } | Select-Object -First 1
        ($fed.files[0] | ConvertFrom-Json).subject | Should -Be 'repo:test@41/target@42:environment:preview'
    }

    It 'a federated credential of the same name with a stale subject is updated, not created again' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        # As J-019's redeploy: the credential made for the plain subject holds the name, and create fails on it.
        $name = "preview-$($script:Repo -replace '[^A-Za-z0-9-]', '-')"
        $oidc = Join-Path $script:GitBase "$script:Repo.oidc.json"
        Set-Content $oidc '{"use_default":true,"use_immutable_subject":true,"sub_claim_prefix":"repo:test@41/target@42"}'
        $env:FACTORY_TOOL_LIST = ConvertTo-Json -Compress @(@{ name = $name; subject = "repo:$($script:Repo):environment:preview" })
        try { F deploy $id | Out-Null } finally { Remove-Item $oidc; $env:FACTORY_TOOL_LIST = $null }

        $calls = @(ToolCalls)
        $calls | Where-Object { $_.args -contains 'create' } | Should -BeNullOrEmpty
        $upd = $calls | Where-Object { $_.args -contains 'update' } | Select-Object -First 1
        ($upd.args -join ' ') | Should -Match "ad app federated-credential update --id gh-app-client --federated-credential-id $name"
        ($upd.files[0] | ConvertFrom-Json).subject | Should -Be 'repo:test@41/target@42:environment:preview'
    }

    It 'a GitHub PAT missing a permission flags the job github-permission naming it, keeps the attempt, and creates nothing in HCP' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        $deny = Join-Path $script:GitBase "$script:Repo.deny"
        Set-Content $deny 'environments=write'
        try { F deploy $id | Out-Null } finally { Remove-Item $deny }
        $LASTEXITCODE | Should -Be 1

        $j = Job $id
        $j.stage | Should -Be 'blocked'
        $j.flag.reason | Should -Be 'github-permission'
        $j.flag.detail | Should -Match 'the GitHub PAT needs environments=write'
        $j.deploy.attempts | Should -Be 0
        HcpWorkspace "$(RepoName)-j001" | Should -BeNullOrEmpty
        Get-Content (Join-Path (JobDir $id) 'deploy-1.log') -Raw | Should -Match 'environments=write'
    }

    It 'gh refusing the TF_API_TOKEN secret with HTTP 403 names secrets=write' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        $env:FACTORY_TOOL_FAIL = '^secret set|failed to set secret: HTTP 403: Resource not accessible by personal access token'
        try { F deploy $id | Out-Null } finally { Remove-Item Env:FACTORY_TOOL_FAIL }

        $j = Job $id
        $j.flag.reason | Should -Be 'github-permission'
        $j.flag.detail | Should -Match 'the GitHub PAT needs secrets=write'
    }

    It 'redeploy, after the permission is added, queues the job to resume at deploy, and the deploy goes through' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        $deny = Join-Path $script:GitBase "$script:Repo.deny"
        Set-Content $deny 'actions=write'
        try { F deploy $id | Out-Null } finally { Remove-Item $deny }
        (Job $id).flag.detail | Should -Match 'actions=write'
        $round = (Job $id).round

        F redeploy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        $j = Job $id
        $j.stage | Should -Be 'approved'
        $j.resume | Should -Be 'deploy'
        $j.round | Should -Be $round
        $j.flag | Should -BeNullOrEmpty

        # The runner's move: approved -> deploy, not build, and resume is cleared.
        F set-stage $id deploy -By runner | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'deploy'
        (Job $id).resume | Should -BeNullOrEmpty

        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'approve'
        (Job $id).deploy.attempts | Should -Be 1
    }

    It 'redeploy after the attempts ran out gives the job a fresh budget, and the deploy goes through' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        # As J-019: its attempts spent on factory and template bugs, not on the change.
        Commit-Change $id 'deploy-fail.txt' 'exec: "az": executable file not found in $PATH'
        1..5 | ForEach-Object { F deploy $id 2>$null | Out-Null }
        (Job $id).flag.reason | Should -Be 'deploy-failed'

        Commit-Change $id 'deploy-fail.txt' -Remove
        F redeploy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).deploy.attempts | Should -Be 0
        F set-stage $id deploy -By runner | Out-Null
        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'approve'
    }

    It 'redeploy sends a job escalated out of deploy back to deploy, with a fresh attempt budget' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        Commit-Change $id 'deploy-fail.txt' 'factory bug'
        F deploy $id | Out-Null
        F set-stage $id ready -By orchestrator -Reason escalated -Detail 'devops gave up' | Out-Null
        (Job $id).flag.reason | Should -Be 'escalated'

        Commit-Change $id 'deploy-fail.txt' -Remove
        F redeploy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'approved'
        (Job $id).deploy.attempts | Should -Be 0
        F set-stage $id deploy -By runner | Out-Null
        F deploy $id | Out-Null
        (Job $id).stage | Should -Be 'approve'
        (Job $id).deploy.attempts | Should -Be 1
    }

    It 'the runner cannot move an ordinary approved job to deploy' {
        $id = New-ApprovedJob
        F set-stage $id deploy -By runner 2>$null | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'approved'
    }

    It 'redeploy refuses a job escalated from a stage other than deploy' {
        $id = New-ApprovedJob
        Move-ToReview $id
        F set-stage $id ready -By orchestrator -Reason escalated -Detail 'reviewer stuck' | Out-Null
        (Job $id).stage | Should -Be 'blocked'
        F redeploy $id 2>$null | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'blocked'
    }

    It 'redeploy refuses a job that is not flagged github-permission' {
        $id = New-ApprovedJob
        F redeploy $id 2>$null | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'approved'
    }

    It 'a failed run writes deploy-1.log from GitHub and HCP, keeps the job in deploy, and exits 1' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        Commit-Change $id 'deploy-fail.txt' 'probe on /health fails every revision'

        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Be 1

        $j = Job $id
        $j.stage | Should -Be 'deploy'
        $j.deploy.status | Should -Be 'failed'
        $log = Get-Content (Join-Path (JobDir $id) 'deploy-1.log') -Raw
        $log | Should -Match '## GitHub'
        $log | Should -Match '## HCP'
        ([regex]::Matches($log, 'probe on /health fails every revision')).Count | Should -Be 2
        (F deploy-log $id) -join "`n" | Should -Match 'probe on /health'
        (Events | Where-Object { $_.kind -eq 'deploy' -and $_.job -eq $id }).detail | Should -Match '^failed \(attempt 1\): '
    }

    It 'a fix and a second attempt get the job to approve' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        Commit-Change $id 'deploy-fail.txt' 'boom'
        F deploy $id | Out-Null

        Commit-Change $id 'deploy-fail.txt' -Remove
        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Be 0

        (Job $id).stage | Should -Be 'approve'
        (Job $id).deploy.attempts | Should -Be 2
        (Job $id).deploy.status | Should -Be 'up'
    }

    It 'the fifth failed attempt moves the job to blocked flagged deploy-failed, and a sixth is refused' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        Commit-Change $id 'deploy-fail.txt' 'boom'
        1..5 | ForEach-Object { F deploy $id | Out-Null }

        $j = Job $id
        $j.stage | Should -Be 'blocked'
        $j.flag.reason | Should -Be 'deploy-failed'
        $j.flag.detail | Should -Match 'boom'
        $j.deploy.attempts | Should -Be 5
        Test-Path (Join-Path (JobDir $id) 'deploy-5.log') | Should -BeTrue

        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).deploy.attempts | Should -Be 5
    }

    It 'destroy runs a destroy, deletes the workspace and records down; again it is a no-op' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        F deploy $id | Out-Null
        $ws = HcpWorkspace "$(RepoName)-j001"

        F destroy $id | Out-Null
        $LASTEXITCODE | Should -Be 0

        (Job $id).deploy.status | Should -Be 'down'
        (Job $id).stage | Should -Be 'approve'
        (DeployState).hcpRuns.Values | Where-Object { $_.ws -eq $ws.id -and $_.isDestroy } | Should -Not -BeNullOrEmpty
        (DeployState).workspaces[$ws.id].deleted | Should -BeTrue
        (Events | Where-Object { $_.kind -eq 'destroy' -and $_.job -eq $id }).detail | Should -Be 'down'

        F destroy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        @(Events | Where-Object { $_.kind -eq 'destroy' -and $_.job -eq $id }).Count | Should -Be 1
    }

    It 'a deploy whose process died mid-run resumes that attempt: waits on its run, dispatches none, counts none' {
        # J-019: the devops agent's shell call ended at 20 minutes, killing a deploy whose run takes ~60;
        # each retry dispatched another run and spent an attempt.
        $id = New-ApprovedJob
        Move-ToDeploy $id
        function Runs { $(try { @((DeployState).runs.Values | Where-Object { $_.head_branch -eq "factory/$id" -and $_.repo -eq $script:Repo }).Count } catch { 0 }) }
        $slow = Join-Path $script:GitBase "$script:Repo.slow"
        Set-Content $slow ''
        $p = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $script:Factory, 'deploy', $id -PassThru -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Runs) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
        Stop-Process -Id $p.Id -Force
        Runs | Should -Be 1
        (Job $id).deploy.status | Should -Be 'applying'

        Remove-Item $slow
        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        Runs | Should -Be 1
        (Job $id).deploy.attempts | Should -Be 1
        (Job $id).deploy.status | Should -Be 'up'
        (Job $id).stage | Should -Be 'approve'
    }

    It 'a resumed deploy whose run already finished takes that run''s result: dispatches none, counts none' {
        # J-023: a fresh preview's run took 28 minutes; the devops agent's shell call died at 10 and the
        # run succeeded 7 minutes after the job escalated. Resuming dispatched a second, needless run.
        $id = New-ApprovedJob
        Move-ToDeploy $id
        # The stub writes deploy-state.json at its first dispatch; until then there are no runs.
        function Runs { $ErrorActionPreference = 'Stop'; $(try { @((DeployState).runs.Values | Where-Object { $_.head_branch -eq "factory/$id" -and $_.repo -eq $script:Repo }) } catch { @() }) }
        $slow = Join-Path $script:GitBase "$script:Repo.slow"
        Set-Content $slow ''
        $p = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $script:Factory, 'deploy', $id -PassThru -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds(60)
        while (-not @(Runs).Count -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
        Stop-Process -Id $p.Id -Force
        # Dead before the run can finish: a still-dying deploy would otherwise poll it and record the result.
        $p.WaitForExit(30000) | Out-Null
        Remove-Item $slow
        $run = @(Runs)[0]
        # Nobody is waiting on it: one look at the run completes it, as GitHub would have meanwhile.
        (Invoke-RestMethod "http://localhost:$script:GhPort/repos/$script:Repo/actions/runs/$($run.id)" -Headers @{ Authorization = "Bearer $script:Token" }).status | Should -Be 'completed'
        (Job $id).deploy.status | Should -Be 'applying'

        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        @(Runs).Count | Should -Be 1
        (Job $id).deploy.attempts | Should -Be 1
        (Job $id).deploy.status | Should -Be 'up'
        (Job $id).stage | Should -Be 'approve'
    }

    It 'a destroy that errors records HCP''s own error, not just its status' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        F deploy $id | Out-Null
        Set-Content (Join-Path $script:GitBase 'destroy-fails') ''
        try { F destroy $id | Out-Null } finally { Remove-Item (Join-Path $script:GitBase 'destroy-fails') }
        $LASTEXITCODE | Should -Not -Be 0
        (Events | Where-Object { $_.kind -eq 'destroy' -and $_.job -eq $id }).detail |
            Should -Be 'failed: destroy run ended errored: Error: deleting Resource Group "rg-x": the Resource Group still contains Resources.'
    }

    Context 'the deploy loop' {
        BeforeEach {
            # A job in deploy whose last attempt failed.
            function New-FailedDeploy {
                $id = New-ApprovedJob
                Move-ToDeploy $id
                Commit-Change $id 'deploy-fail.txt' 'boom'
                F deploy $id | Out-Null
                (Job $id).deploy.status | Should -Be 'failed'
                $id
            }
            function Diagnosis {
                $p = Join-Path $TestDrive "diagnosis-$([guid]::NewGuid().ToString('N')).md"
                Set-Content $p "The migration fails: column already exists.`nThe builder should drop the duplicate."
                $p
            }
        }

        It 'deploy-fix sends the job back to build as a new round, with the diagnosis, reviews pending and no attempts used' {
            $id = New-FailedDeploy
            F deploy-fix $id -File (Diagnosis) | Out-Null
            $LASTEXITCODE | Should -Be 0

            $j = Job $id
            $j.stage | Should -Be 'build'
            $j.round | Should -Be 2
            $j.deployFixes | Should -Be 1
            $j.deploy.attempts | Should -Be 0
            $j.reviews.code | Should -Be 'pending'
            Get-Content (Join-Path (JobDir $id) 'deploy-fix-2.md') -Raw | Should -Match 'column already exists'
        }

        It 'the third deploy-fix blocks the job with deploy-fix-rounds, and it does not start a round' {
            $id = New-FailedDeploy
            foreach ($n in 2, 3) {
                F deploy-fix $id -File (Diagnosis) | Out-Null
                (Job $id).round | Should -Be $n
                Write-Build $id
                F set-stage $id review | Out-Null
                Write-Reviews $id (All 'Verdict: PASS')
                F read-reviews $id | Out-Null
                F deploy $id | Out-Null
            }
            F deploy-fix $id -File (Diagnosis) | Out-Null
            $LASTEXITCODE | Should -Be 0
            $j = Job $id
            $j.stage | Should -Be 'blocked'
            $j.flag.reason | Should -Be 'deploy-fix-rounds'
            $j.round | Should -Be 3
            $j.deployFixes | Should -Be 2
        }

        It 'a CHANGES verdict in a deploy-fix round blocks the job with max-rounds, like a rework round' {
            $id = New-FailedDeploy
            F deploy-fix $id -File (Diagnosis) | Out-Null
            Write-Build $id
            F set-stage $id review | Out-Null
            Write-Reviews $id (All 'Verdict: CHANGES')
            F read-reviews $id | Out-Null
            (Job $id).stage | Should -Be 'blocked'
            (Job $id).flag.reason | Should -Be 'max-rounds'
        }

        It 'a rework resets deployFixes' {
            $id = New-FailedDeploy
            F deploy-fix $id -File (Diagnosis) | Out-Null
            Write-Build $id
            F set-stage $id review | Out-Null
            Write-Reviews $id (All 'Verdict: CHANGES')
            F read-reviews $id | Out-Null
            F rework $id 'do it differently' | Out-Null
            (Job $id).deployFixes | Should -Be 0
        }

        It 'deploy-fix and block are refused unless the last attempt failed, so an agent cannot skip the deploy' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy-fix $id -File (Diagnosis) | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
            F block $id -Detail 'a quota' | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
            (Job $id).stage | Should -Be 'deploy'
            (Job $id).round | Should -Be 1
        }

        It 'deploy-fix and block are refused outside deploy' {
            $id = New-ApprovedJob
            F deploy-fix $id -File (Diagnosis) | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
            F block $id -Detail 'a quota' | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
            (Job $id).stage | Should -Be 'approved'
        }

        It 'deploy-fix needs a diagnosis, and block needs a reason' {
            $id = New-FailedDeploy
            F deploy-fix $id -File 'no-such-file.md' | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
            F block $id | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
            (Job $id).stage | Should -Be 'deploy'
        }

        It 'block parks the job with environment and the reason; redeploy then keeps its round and deployFixes' {
            $id = New-FailedDeploy
            F deploy-fix $id -File (Diagnosis) | Out-Null
            Write-Build $id
            F set-stage $id review | Out-Null
            Write-Reviews $id (All 'Verdict: PASS')
            F read-reviews $id | Out-Null
            F deploy $id | Out-Null

            F block $id -Detail 'the region is out of B1ms capacity' | Out-Null
            $LASTEXITCODE | Should -Be 0
            $j = Job $id
            $j.stage | Should -Be 'blocked'
            $j.flag.reason | Should -Be 'environment'
            $j.flag.detail | Should -Be 'the region is out of B1ms capacity'

            F redeploy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            $j = Job $id
            $j.stage | Should -Be 'approved'
            $j.resume | Should -Be 'deploy'
            $j.round | Should -Be 2
            $j.deployFixes | Should -Be 1
            $j.deploy.attempts | Should -Be 0
        }
    }

    Context 'preview teardown' {
        BeforeEach {
            # A discarded job whose destroy has failed, its preview left standing, and HCP still failing it.
            function New-FailedTeardown {
                $id = New-ApprovedJob
                Move-ToDeploy $id
                Commit-Change $id 'deploy-fail.txt' 'boom'
                1..5 | ForEach-Object { F deploy $id | Out-Null }
                Set-Content (Join-Path $script:GitBase 'destroy-fails') ''
                F discard $id | Out-Null
                $deadline = (Get-Date).AddSeconds(30)
                while (-not (Job $id).deploy['destroyFailures']) {
                    if ((Get-Date) -gt $deadline) { throw "the destroy of $id never failed" }
                    Start-Sleep -Milliseconds 300
                }
                $id
            }
        }
        AfterEach {
            foreach ($f in 'destroy-fails', 'destroy-hangs', 'destroy-no-config', 'destroy-nothing', 'safe-delete-busy') {
                Remove-Item (Join-Path $script:GitBase $f) -ErrorAction SilentlyContinue
            }
        }

        It 'a failed destroy is recorded on the job, and a later one clears it' {
            $id = New-FailedTeardown
            $d = (Job $id).deploy
            $d.status | Should -Be 'failed'
            $d.destroyFailures | Should -Be 1
            $d.destroyError | Should -Match 'still contains Resources'

            Remove-Item (Join-Path $script:GitBase 'destroy-fails')
            F destroy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            $d = (Job $id).deploy
            $d.status | Should -Be 'down'
            $d.destroyFailures | Should -Be 0
            $d.destroyError | Should -BeNullOrEmpty
        }

        It 'sweep retries a failed destroy of a finished job' {
            $id = New-FailedTeardown
            Remove-Item (Join-Path $script:GitBase 'destroy-fails')

            F sweep | Should -Be $id
            $LASTEXITCODE | Should -Be 0
            Wait-Preview $id 'down'
            (Job $id).stage | Should -Be 'discarded'
            (HcpWorkspace "$(RepoName)-$((Job $id).deploy.env)").deleted | Should -BeTrue
        }

        It 'sweep leaves the preview of a job still in progress alone' {
            # Its next round applies over the preview; a destroy here is the J-022 race.
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            (Job $id).stage | Should -Be 'approve'

            F sweep | Should -BeNullOrEmpty
            $LASTEXITCODE | Should -Be 0
            (Job $id).deploy.status | Should -Be 'up'
        }

        It 'sweep stops at the fifth failure, which waits for the human' {
            $id = New-FailedTeardown
            1..4 | ForEach-Object { F destroy $id | Out-Null }
            (Job $id).deploy.destroyFailures | Should -Be 5

            F sweep | Should -BeNullOrEmpty
            $LASTEXITCODE | Should -Be 0
        }

        It 'sweep skips a destroy that may still be running, and takes over one that has died' {
            $id = New-FailedTeardown
            Remove-Item (Join-Path $script:GitBase 'destroy-fails')
            $env:FACTORY_DEPLOY_POLL_SECONDS = '1'   # at 0, polling a run that hangs runs the machine out of ports
            Set-Content (Join-Path $script:GitBase 'destroy-hangs') ''
            $p = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $script:Factory, 'destroy', $id -PassThru -WindowStyle Hidden
            Wait-Preview $id 'destroying'
            F sweep | Should -BeNullOrEmpty

            Stop-Process -Id $p.Id -Force
            $p.WaitForExit(30000) | Out-Null
            Remove-Item (Join-Path $script:GitBase 'destroy-hangs')
            $env:FACTORY_DESTROY_STALE_MINUTES = '0'
            try { F sweep | Should -Be $id } finally { Remove-Item Env:FACTORY_DESTROY_STALE_MINUTES }
            Wait-Preview $id 'down'
        }

        It 'serve.ps1 sweeps at startup' {
            $id = New-FailedTeardown
            Remove-Item (Join-Path $script:GitBase 'destroy-fails')
            $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
            $serve = Start-Process pwsh -ArgumentList '-NoProfile', '-File', (Join-Path (Split-Path $script:Factory) 'serve.ps1'), '-Port', $port,
                '-StateDir', $env:FACTORY_HOME, '-EnvFile', $env:FACTORY_ENV_FILE -PassThru -WindowStyle Hidden
            try { Wait-Preview $id 'down' }
            finally { Stop-Process -Id $serve.Id -Force -ErrorAction SilentlyContinue; $serve.WaitForExit(10000) | Out-Null }
        }

        It 'sweep does nothing with deploys off' {
            $env:FACTORY_ENV_FILE = $script:EnvFile
            F sweep | Should -BeNullOrEmpty
            $LASTEXITCODE | Should -Be 0
        }

        It 'destroy waits out the 409 HCP answers while it processes the state (J-025)' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            Set-Content (Join-Path $script:GitBase 'safe-delete-busy') '2'

            F destroy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Job $id).deploy.status | Should -Be 'down'
            (HcpWorkspace "$(RepoName)-$((Job $id).deploy.env)").deleted | Should -BeTrue
        }

        It 'destroy takes planned_and_finished as nothing left to destroy (J-025, on the retry after its 409)' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            Set-Content (Join-Path $script:GitBase 'destroy-nothing') ''

            F destroy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Job $id).deploy.status | Should -Be 'down'
            (Job $id).deploy.destroyError | Should -BeNullOrEmpty
            (HcpWorkspace "$(RepoName)-$((Job $id).deploy.env)").deleted | Should -BeTrue
        }

        It 'destroy deletes a workspace no apply ever reached (J-018: Configuration version is missing)' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            Set-Content (Join-Path $script:GitBase 'destroy-no-config') ''

            F destroy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Job $id).deploy.status | Should -Be 'down'
            (HcpWorkspace "$(RepoName)-$((Job $id).deploy.env)").deleted | Should -BeTrue
        }
    }

    It 'destroy is a no-op for a job that never deployed' {
        $id = New-ApprovedJob
        F destroy $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        @(Events | Where-Object { $_.kind -eq 'destroy' }).Count | Should -Be 0
    }

    It 'qa-login prints the preview QA user and its sensitive password' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        F deploy $id | Out-Null
        # The dashboard's serve.ps1 may hold the HCP token without HCP_ORG (deploys off): the
        # workspace id recorded at deploy time is enough.
        $noOrg = Join-Path $TestDrive "noorg-$([guid]::NewGuid().ToString('N')).env"
        Set-Content $noOrg (Get-Content $env:FACTORY_ENV_FILE | Where-Object { $_ -notmatch '^HCP_ORG=' })
        $env:FACTORY_ENV_FILE = $noOrg

        $out = (F qa-login $id) -join "`n"
        $out | Should -Match "qa-j001@$(RepoName).test"
        $out | Should -Match 'pw-j001'
    }

    It 'merging the job tears its preview down' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        F deploy $id | Out-Null
        Set-Content (Join-Path (JobDir $id) 'decision.md') "Decision: APPROVE`n"
        F read-decision $id | Out-Null
        F gate $id | Out-Null
        (Job $id).stage | Should -Be 'ready'

        F merge $id | Out-Null
        (Job $id).stage | Should -Be 'done'
        Wait-Preview $id 'down'
    }

    It 'discarding the job tears its preview down' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        Commit-Change $id 'deploy-fail.txt' 'boom'
        1..5 | ForEach-Object { F deploy $id | Out-Null }

        F discard $id | Out-Null
        Wait-Preview $id 'down'
    }

    It 'a rework leaves the preview up and starts the next round with no attempts used' {
        # J-022: a detached destroy raced the next round's apply on the same workspace, which then
        # rebuilt from nothing behind it. The next deploy applies over the preview instead.
        $id = New-ApprovedJob
        Move-ToDeploy $id
        F deploy $id | Out-Null
        F set-stage $id ready -Reason escalated -Detail 'needs a human' | Out-Null

        F rework $id 'fix the probe' | Out-Null
        (Job $id).stage | Should -Be 'approved'
        (Job $id).deploy.attempts | Should -Be 0
        Start-Sleep -Seconds 3
        (Job $id).deploy.status | Should -Be 'up'
        @(Events | Where-Object { $_.job -eq $id -and $_.kind -eq 'destroy' }) | Should -HaveCount 0
    }

    Context 'since (front-end-only deploys)' {
        BeforeAll {
            # Deploys the job, reworks it, and deploys round 2 over the live preview. Returns the
            # round-1 applied sha and the inputs of each dispatched run, oldest first.
            function Deploy-Twice([string]$id, [switch]$DeclareSince) {
                Move-ToDeploy $id
                if ($DeclareSince) {
                    New-Item -ItemType Directory -Force (Join-Path (Work $id) '.github/workflows') | Out-Null
                    Commit-Change $id '.github/workflows/deploy.yml' "on:`n  workflow_dispatch:`n    inputs:`n      environment:`n      since:`n"
                }
                F deploy $id | Out-Null
                $LASTEXITCODE | Should -Be 0
                $sha1 = (Job $id).deploy.sha
                F set-stage $id ready -Reason escalated -Detail 'polish' | Out-Null
                F rework $id 'tweak the copy' | Out-Null
                Move-ToDeploy $id
                F deploy $id | Out-Null
                $LASTEXITCODE | Should -Be 0
                $runs = @((DeployState).runs.Values | Where-Object { $_.head_branch -eq "factory/$id" -and $_.repo -eq $script:Repo } | Sort-Object { [long]$_.id })
                @{ sha1 = $sha1; inputs = @($runs | ForEach-Object { $_.inputs }) }
            }
        }

        It 'a redeploy over a live preview passes since = the head it was applied at, when deploy.yml declares since' {
            $id = New-ApprovedJob
            $r = Deploy-Twice $id -DeclareSince
            $r.inputs | Should -HaveCount 2
            $r.inputs[0].Contains('since') | Should -BeFalse
            $r.inputs[1].since | Should -Be $r.sha1
        }

        It 'sends no since when deploy.yml does not declare it, so GitHub does not refuse the dispatch' {
            $id = New-ApprovedJob
            $r = Deploy-Twice $id
            $r.inputs | Should -HaveCount 2
            $r.inputs[1].Contains('since') | Should -BeFalse
        }

        It 'sends no since after a failed attempt: the preview may be half applied' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            New-Item -ItemType Directory -Force (Join-Path (Work $id) '.github/workflows') | Out-Null
            Commit-Change $id '.github/workflows/deploy.yml' "on:`n  workflow_dispatch:`n    inputs:`n      environment:`n      since:`n"
            Commit-Change $id 'deploy-fail.txt' 'boom'
            F deploy $id | Out-Null
            Commit-Change $id 'deploy-fail.txt' -Remove
            F deploy $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            $runs = @((DeployState).runs.Values | Where-Object { $_.head_branch -eq "factory/$id" -and $_.repo -eq $script:Repo } | Sort-Object { [long]$_.id })
            $runs[-1].inputs.Contains('since') | Should -BeFalse
        }
    }

    It 'escalation from deploy is allowed, as from every Building stage, and parks the job in blocked' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        F set-stage $id ready -Reason escalated -Detail 'claude died' | Out-Null
        (Job $id).stage | Should -Be 'blocked'
        (Job $id).flag.reason | Should -Be 'escalated'
    }

    Context 'blocked' {
        It 'max-rounds on a deployable job parks it in blocked' {
            $id = New-ApprovedJob
            Move-ToReview $id
            Write-Reviews $id (All 'Verdict: CHANGES')
            F read-reviews $id | Out-Null
            Write-Build $id
            F set-stage $id review | Out-Null
            Write-Reviews $id (All 'Verdict: CHANGES')
            F read-reviews $id | Should -Be 'blocked'
            (Job $id).flag.reason | Should -Be 'max-rounds'
        }

        It 'escalation from <from> parks a deployable job in blocked' -ForEach @(@{ from = 'build' }, @{ from = 'review' }) {
            $id = New-ApprovedJob
            if ($from -eq 'build') { F set-stage $id build -By runner | Out-Null } else { Move-ToReview $id }
            F set-stage $id ready -Reason escalated -Detail 'gave up' | Out-Null
            (Job $id).stage | Should -Be 'blocked'
        }

        It 'escalation from approve lands in ready: the preview is up' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F deploy $id | Out-Null
            (Job $id).stage | Should -Be 'approve'
            F set-stage $id ready -Reason escalated -Detail 'approver died' | Out-Null
            (Job $id).stage | Should -Be 'ready'
        }

        It 'an escalated bootstrap job lands in ready: it is not deployable' {
            $i = F new-idea 'boot' -Target "test/boot-$([guid]::NewGuid().ToString('N').Substring(0,8))" -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false}'
            $boot = ($i -split ' ')[1]
            F approve $boot | Out-Null
            F set-stage $boot build -By runner | Out-Null
            F set-stage $boot ready -Reason escalated -Detail 'gave up' | Out-Null
            (Job $boot).stage | Should -Be 'ready'
        }

        It 'does not hold the queue: the next approved job can build' {
            $a = New-ApprovedJob 'A'
            F set-stage $a build -By runner | Out-Null
            F set-stage $a ready -Reason escalated -Detail 'gave up' | Out-Null
            (Job $a).stage | Should -Be 'blocked'
            $b = New-ApprovedJob 'B'
            F set-stage $b build -By runner | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Job $b).stage | Should -Be 'build'
        }

        It 'rework moves it back to approved with the flag cleared and a new round' {
            $id = New-ApprovedJob
            Move-ToReview $id
            F set-stage $id ready -Reason escalated -Detail 'gave up' | Out-Null
            F rework $id 'try again' | Out-Null
            $LASTEXITCODE | Should -Be 0
            $j = Job $id
            $j.stage | Should -Be 'approved'
            $j.flag | Should -BeNullOrEmpty
            $j.round | Should -Be 2
        }

        It 'discard discards it' {
            $id = New-ApprovedJob
            Move-ToReview $id
            F set-stage $id ready -Reason escalated -Detail 'gave up' | Out-Null
            F discard $id | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Job $id).stage | Should -Be 'discarded'
        }

        It 'publish opens its PR' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F set-stage $id ready -Reason escalated -Detail 'gave up' | Out-Null
            F publish $id | Should -Match '/pull/'
            $LASTEXITCODE | Should -Be 0
            (Job $id).pr.number | Should -Not -BeNullOrEmpty
        }

        It 'merge refuses it: only Ready for review is accepted' {
            $id = New-ApprovedJob
            Move-ToDeploy $id
            F set-stage $id ready -Reason escalated -Detail 'gave up' | Out-Null
            F merge $id | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
            (Job $id).stage | Should -Be 'blocked'
        }
    }
}

Describe 'lessons' {
    BeforeAll {
        function Write-DeployReport([string]$id, [int]$round, [string]$gaps) {
            $dir = Join-Path (JobDir $id) "round-$round"
            New-Item -ItemType Directory -Force $dir | Out-Null
            Set-Content (Join-Path $dir 'deploy.md') "# Deploy: $id, round $round`n`n## Attempts`n- Attempt 1: up`n`n## Template gaps`n$gaps`n"
        }
        function Lesson([string]$id) { Get-Content (Join-Path $env:FACTORY_HOME "lessons/$id.json") -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
        function LessonMd([string]$id) { Get-Content (Join-Path $env:FACTORY_HOME "lessons/$id.md") -Raw }
    }

    It 'writes nothing for a job with no flag and no template gaps' {
        $id = New-ApprovedJob
        Move-ToGate $id
        Write-DeployReport $id 1 'None.'
        F gate $id | Out-Null

        F lesson $id | Should -Be 'no lessons'
        Test-Path (Join-Path $env:FACTORY_HOME "lessons/$id.json") | Should -BeFalse
    }

    It 'a bulleted "- None." in deploy.md or build.md is no template gap (J-024 to J-026 counted one)' {
        $id = New-ApprovedJob
        Move-ToGate $id
        Write-DeployReport $id 1 '- None.'
        Set-Content (Join-Path (JobDir $id) 'round-1/build.md') "# Build: $id`n`n## Template gaps`n- none`n"
        F gate $id | Out-Null

        F lesson $id | Should -Be 'no lessons'
    }

    It 'compiles the flag and every template gap from the rounds'' deploy reports' {
        $id = New-ApprovedJob 'Search'
        Move-ToReview $id
        Write-DeployReport $id 1 "- Symptom: probe failed`n  Cause: /health is Development-only`n  Belongs in: template-projects:IaC/containerapp.api.tf`n- Symptom: second gap`n  Belongs in: template-projects:IaC/README.md"
        F set-stage $id ready -Reason escalated -Detail 'claude died mid-deploy' | Out-Null

        F lesson $id | Out-Null
        $LASTEXITCODE | Should -Be 0

        $l = Lesson $id
        $l.job | Should -Be $id
        $l.title | Should -Be 'Search'
        $l.target | Should -Be $script:Repo
        $l.flag | Should -Be 'escalated'
        $l.templateGaps | Should -Be 2
        $l.status | Should -Be 'open'
        $md = LessonMd $id
        $md | Should -Match 'claude died mid-deploy'
        $md | Should -Match 'template-projects:IaC/containerapp.api.tf'
        $md | Should -Match 'second gap'
        (Events | Where-Object { $_.kind -eq 'lesson' -and $_.job -eq $id }).detail | Should -Be '2'
    }

    It 'appends a human evaluation''s lessons with -File' {
        $id = New-ApprovedJob
        Move-ToReview $id
        F set-stage $id ready -Reason escalated -Detail 'stuck' | Out-Null
        $f = Join-Path $TestDrive "lessons-$([guid]::NewGuid().ToString('N')).md"
        Set-Content $f '- The React add-on should ship an empty-state component.'

        F lesson $id -File $f | Out-Null

        $md = LessonMd $id
        $md | Should -Match 'stuck'
        $md | Should -Match '## From the evaluation'
        $md | Should -Match 'empty-state component'
    }

    It 'lesson-mark records the status and the session, and purge keeps lessons' {
        $id = New-ApprovedJob
        Move-ToReview $id
        F set-stage $id ready -Reason escalated -Detail 'stuck' | Out-Null
        F lesson $id | Out-Null

        F lesson-mark $id proposed -Detail 'S-004' | Out-Null
        (Lesson $id).status | Should -Be 'proposed'
        (Lesson $id).session | Should -Be 'S-004'

        F lesson-mark $id bogus | Out-Null
        $LASTEXITCODE | Should -Not -Be 0

        F discard $id | Out-Null
        Evaluate @($id)
        F purge | Out-Null
        Test-Path (Join-Path $env:FACTORY_HOME "lessons/$id.json") | Should -BeTrue
    }
}

}

Describe 'First-Error' {
    BeforeAll {
        . (Join-Path $PSScriptRoot '../scripts/deploy.ps1')
        $e = [char]27
        # GitHub prints a step's script in cyan before running it; J-019's attempt 3 flag quoted this echo.
        $script:Echo = "2026-10-24T17:00:55.1988402Z $e[36;1m  echo `"::error::environment must be dev, staging or j<NNN>; got '`$TF_ENV'`"$e[0m"
    }

    It 'skips a step script echoed by GitHub and finds the Terraform diagnostic' {
        $log = @(
            $script:Echo
            "2026-10-24T17:01:57.2015297Z $e[31m│$e[0m $e[0m$e[1m$e[31mError: $e[0m$e[0m$e[1munable to build authorizer for Resource Manager API: could not configure AzureCli Authorizer$e[0m"
            '2026-10-24T17:02:07.3340748Z ##[error]Process completed with exit code 1.'
        ) -join "`n"
        First-Error $log | Should -Be 'Error: unable to build authorizer for Resource Manager API: could not configure AzureCli Authorizer'
    }

    It 'takes an action''s ##[error] line, without its timestamp and marker' {
        $log = @(
            $script:Echo
            "2026-10-24T13:24:01.9992159Z ##[error]AADSTS700213: No matching federated identity record found for presented assertion subject 'repo:o@1/n@2:environment:preview'."
            '2026-10-24T13:24:02.1060520Z ##[error]Login failed with Error: The process ''/usr/bin/az'' failed with exit code 1.'
        ) -join "`n"
        First-Error $log | Should -Be "AADSTS700213: No matching federated identity record found for presented assertion subject 'repo:o@1/n@2:environment:preview'."
    }

    It 'ignores the word error in a Terraform warning, and finds the failing step''s own output (canary 21)' {
        $log = @(
            "2026-11-06T21:32:25.8579910Z $e[33m│$e[0m $e[0mexceptional situations such as recovering from errors or mistakes, or when"
            "2026-11-06T21:32:25.8580584Z $e[33m│$e[0m $e[0mTerraform specifically suggests to use it as part of an error message."
            "2026-11-06T21:37:43.4937425Z $e[36;1mpsql -v ON_ERROR_STOP=1 -q -v role=`"`$DB_ROLE`"$e[0m"
            '2026-11-06T21:38:03.0000000Z ##[group]Run dotnet ef database update'
            '2026-11-06T21:38:03.8921342Z Npgsql.PostgresException (0x80004005): 42501: permission denied for schema public'
            '2026-11-06T21:38:03.9295807Z     Severity: ERROR'
            '2026-11-06T21:38:03.9441792Z 42501: permission denied for schema public'
            '2026-11-06T21:38:03.9442112Z '
            '2026-11-06T21:38:03.9442298Z POSITION: 28'
            '2026-11-06T21:38:03.9652543Z ##[error]Process completed with exit code 1.'
        ) -join "`n"
        First-Error $log | Should -Be 'Npgsql.PostgresException (0x80004005): 42501: permission denied for schema public'
    }

    It 'ignores a Terraform warning box with no colour too, as HCP''s logs print it' {
        $log = @(
            '╷'
            '│ Warning: Applied changes may be incomplete'
            '│ Terraform specifically suggests to use it as part of an error message.'
            '╵'
            'Plan: 3 to add'
        ) -join "`n"
        First-Error $log | Should -Be 'Plan: 3 to add'
    }

    It 'adds the response to a generic Terraform summary, and skips the with and on lines (canaries 16 and 24)' {
        $log = @(
            '2026-11-06T23:20:00.9577971Z │ Error: Failed to execute action'
            '2026-11-06T23:20:00.9578515Z │ '
            '2026-11-06T23:20:00.9579001Z │   with msgraph_resource_action.user_flow_link,'
            '2026-11-06T23:20:00.9579788Z │   on auth.entra-external.tf line 161, in resource "msgraph_resource_action" "user_flow_link":'
            '2026-11-06T23:20:00.9581143Z │ '
            '2026-11-06T23:20:00.9581538Z │ API call failed: POST'
            '2026-11-06T23:20:00.9582860Z │ https://graph.microsoft.com/v1.0/identity/authenticationEventsFlows/1/conditions/applications/includeApplications'
            '2026-11-06T23:20:00.9584583Z │ RESPONSE 400: 400 Bad Request'
            '2026-11-06T23:20:00.9585033Z │ ERROR CODE: AADB2C'
            '2026-11-06T23:20:00.9586139Z │ {'
            '2026-11-06T23:20:01.0000000Z ╵'
        ) -join "`n"
        First-Error $log | Should -Be 'Error: Failed to execute action: RESPONSE 400: 400 Bad Request; ERROR CODE: AADB2C'
    }

    It 'joins a summary that azurerm wrapped over several lines, so the cause is in it (J-019 attempt 2)' {
        $log = @(
            '│ Error: creating Key Vault (Subscription: "s-1"'
            '│ Resource Group Name: "rg-todoapp-j019"'
            '│ Key Vault Name: "kv-1"): polling after CreateOrUpdate: unexpected status 404 (404 Not Found) with error: ResourceNotFound: The Resource was not found'
            '│ '
            '│   with azurerm_key_vault.main,'
            '╵'
        ) -join "`n"
        First-Error $log | Should -Be 'Error: creating Key Vault (Subscription: "s-1" Resource Group Name: "rg-todoapp-j019" Key Vault Name: "kv-1"): polling after CreateOrUpdate: unexpected status 404 (404 Not Found) with error: ResourceNotFound: The Resource was not found'
    }

    It 'adds the Message when the summary ends by promising the error (J-019 attempt 1)' {
        $log = @(
            '│ Error: creating Flexible Server (Subscription: "s-1"'
            '│ Flexible Server Name: "psql-1"): polling failed: the Azure API returned the following error:'
            '│ '
            '│ Status: "SkuNotAvailable"'
            '│ Message: "Specified SKU is not supported in this region. Please choose a different SKU."'
            '╵'
        ) -join "`n"
        First-Error $log | Should -Be 'Error: creating Flexible Server (Subscription: "s-1" Flexible Server Name: "psql-1"): polling failed: the Azure API returned the following error: Message: "Specified SKU is not supported in this region. Please choose a different SKU."'
    }

    It 'falls back to the generic exit line when nothing better is there' {
        First-Error "deploy`n2026-10-24T17:02:07.3340748Z ##[error]Process completed with exit code 1." | Should -Be 'Process completed with exit code 1.'
    }
}

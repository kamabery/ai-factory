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
        (Events | Where-Object { $_.kind -eq 'note' -and $_.job -eq $id }).detail | Should -Match 'deploy skipped: bootstrap'
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
        ($out -join "`n") | Should -Match 'https://stapp-'

        $j = Job $id
        $j.stage | Should -Be 'approve'
        $j.deploy.status | Should -Be 'up'
        $j.deploy.env | Should -Be 'j001'
        $j.deploy.workspace | Should -Be "$(RepoName)-j001"
        $j.deploy.workspaceId | Should -Be (HcpWorkspace "$(RepoName)-j001").id
        $j.deploy.attempts | Should -Be 1
        $j.deploy.urls.frontend | Should -Be "https://stapp-$(RepoName)-react-j001.test"
        $j.deploy.urls.api | Should -Be "https://ca-$(RepoName)-api.j001.test"
        $j.deploy.urls.health | Should -Be "https://stapp-$(RepoName)-react-j001.test/api/health"
        $j.deploy.sha | Should -Be (git -C (Work $id) rev-parse HEAD)
        $j.deploy.destroyAt | Should -Not -BeNullOrEmpty
        (Events | Where-Object { $_.kind -eq 'deploy' -and $_.job -eq $id }).detail | Should -Be "up https://stapp-$(RepoName)-react-j001.test"
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
        $j.stage | Should -Be 'ready'
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
        # As J-019: three attempts spent on factory and template bugs, not on the change.
        Commit-Change $id 'deploy-fail.txt' 'exec: "az": executable file not found in $PATH'
        1..3 | ForEach-Object { F deploy $id 2>$null | Out-Null }
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
        F redeploy $id 2>$null | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'ready'
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

    It 'the third failed attempt moves the job to ready flagged deploy-failed, and a fourth is refused' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        Commit-Change $id 'deploy-fail.txt' 'boom'
        1..3 | ForEach-Object { F deploy $id | Out-Null }

        $j = Job $id
        $j.stage | Should -Be 'ready'
        $j.flag.reason | Should -Be 'deploy-failed'
        $j.flag.detail | Should -Match 'boom'
        $j.deploy.attempts | Should -Be 3
        Test-Path (Join-Path (JobDir $id) 'deploy-3.log') | Should -BeTrue

        F deploy $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).deploy.attempts | Should -Be 3
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
        1..3 | ForEach-Object { F deploy $id | Out-Null }

        F discard $id | Out-Null
        Wait-Preview $id 'down'
    }

    It 'a rework tears the preview down and starts the next round with no attempts used' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        Commit-Change $id 'deploy-fail.txt' 'boom'
        1..3 | ForEach-Object { F deploy $id | Out-Null }

        F rework $id 'fix the probe' | Out-Null
        (Job $id).stage | Should -Be 'approved'
        (Job $id).deploy.attempts | Should -Be 0
        Wait-Preview $id 'down'
    }

    It 'escalation from deploy is allowed, as from every Building stage' {
        $id = New-ApprovedJob
        Move-ToDeploy $id
        F set-stage $id ready -Reason escalated -Detail 'claude died' | Out-Null
        (Job $id).stage | Should -Be 'ready'
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

    It 'falls back to the generic exit line when nothing better is there' {
        First-Error "deploy`n2026-10-24T17:02:07.3340748Z ##[error]Process completed with exit code 1." | Should -Be 'Process completed with exit code 1.'
    }
}

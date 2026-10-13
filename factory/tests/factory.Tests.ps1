#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

BeforeAll {
    $script:Factory = Join-Path $PSScriptRoot '..\scripts\factory.ps1' | Resolve-Path | Select-Object -ExpandProperty Path

    function F {
        # Runs factory.ps1 in-process; stderr is discarded, $LASTEXITCODE carries the verdict.
        $out = & $script:Factory @args 2>$null
        $out
    }

    function Board { Get-Content (Join-Path $env:FACTORY_HOME 'board.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
    function Job([string]$id) { (Board).jobs | Where-Object { $_.id -eq $id } }
    function Events { Get-Content (Join-Path $env:FACTORY_HOME 'events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable -DateKind String } }
    function JobDir([string]$id) { Join-Path $env:FACTORY_HOME "jobs/$id" }

    function New-Spec([string]$title) {
        $p = Join-Path $TestDrive "spec-$([guid]::NewGuid().ToString('N')).md"
        Set-Content $p "# Spec: $title`n`n## Intent`nDo $title.`n"
        $p
    }

    function New-BacklogJob([string]$title = 'Thing') {
        $idea = F new-idea "idea for $title"
        F add-spec $idea (New-Spec $title)
    }

    function New-ApprovedJob([string]$title = 'Thing') {
        $id = New-BacklogJob $title
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
        F checkout $id | Out-Null
        Set-Content (Join-Path $env:FACTORY_WORKTREE $file) "work for $id"
        git -C $env:FACTORY_WORKTREE add -A 2>&1 | Out-Null
        git -C $env:FACTORY_WORKTREE commit -q -m "work for $id" 2>&1 | Out-Null
    }

    function Gate-Cmds([int]$build = 0, [int]$test = 0, [int]$ui = 0) {
        ConvertTo-Json -Compress @(
            @{ name = 'build'; cmd = "exit $build" }
            @{ name = 'test'; cmd = "exit $test" }
            @{ name = 'ui'; cmd = "exit $ui" }
        )
    }
}

Describe 'factory.ps1' {

BeforeEach {
    $script:Repo = Join-Path $TestDrive "repo-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory $script:Repo | Out-Null
    git -C $script:Repo init -q -b main 2>&1 | Out-Null
    git -C $script:Repo config user.email 'factory@test' | Out-Null
    git -C $script:Repo config user.name 'factory test' | Out-Null
    git -C $script:Repo config core.autocrlf false | Out-Null
    Set-Content (Join-Path $script:Repo 'README.md') 'fixture'
    git -C $script:Repo add -A 2>&1 | Out-Null
    git -C $script:Repo commit -q -m init 2>&1 | Out-Null

    $env:FACTORY_HOME = Join-Path $TestDrive "state-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $env:FACTORY_WORKTREE = "$script:Repo.wt"
    $env:FACTORY_GATE_CMDS = Gate-Cmds
    Push-Location $script:Repo
}

AfterEach {
    Pop-Location
    Remove-Item Env:FACTORY_HOME, Env:FACTORY_WORKTREE, Env:FACTORY_GATE_CMDS -ErrorAction SilentlyContinue
}

Describe 'ideas and specs' {
    It 'allocates I-001, then J-001 and J-002 in backlog, titled from the spec heading' {
        $idea = F new-idea 'find books fast'
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
        $a = F new-idea 'good'
        $b = F new-idea 'vague'
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
        $new = Join-Path $TestDrive 'edited.md'
        Set-Content $new '# Spec: edited'
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
        $j.gate.sha | Should -Be (git rev-parse "factory/$id")
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
}

Describe 'merge' {
    It 'clean tree and green gate: --no-ff merge commit on main, job done' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Job $id).stage | Should -Be 'done'
        git rev-list --parents -n 1 main | ForEach-Object { ($_ -split ' ').Count } | Should -Be 3
        Test-Path (Join-Path $script:Repo 'feature.txt') | Should -BeTrue
        (Events | Where-Object kind -eq 'merge').job | Should -Be $id
    }

    It 'dirty tree: stays ready flagged merge-blocked, nothing merged' {
        $id = New-ApprovedJob
        Commit-OnBranch $id
        Move-ToGate $id
        F gate $id | Out-Null
        $head = git rev-parse main
        Add-Content (Join-Path $script:Repo 'README.md') 'dirty'
        F merge $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'ready'
        (Job $id).flag.reason | Should -Be 'merge-blocked'
        git rev-parse main | Should -Be $head
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
        $head = git rev-parse main

        F merge $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        (Job $id).stage | Should -Be 'ready'
        (Job $id).flag.reason | Should -Be 'gate-failed'
        git rev-parse main | Should -Be $head
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

    It 'discard works from backlog and from ready, and deletes the branch' {
        $a = New-BacklogJob 'A'
        F discard $a | Out-Null
        (Job $a).stage | Should -Be 'discarded'

        $b = New-ApprovedJob 'B'
        Commit-OnBranch $b
        F set-stage $b build -By runner | Out-Null
        F set-stage $b ready -Reason escalated -Detail 'x' | Out-Null
        F discard $b | Out-Null
        (Job $b).stage | Should -Be 'discarded'
        git branch --list "factory/$b" | Should -BeNullOrEmpty
    }

    It 'discard is refused from building stages' {
        $id = New-ApprovedJob
        F set-stage $id build -By runner | Out-Null
        F discard $id | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
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

    It 'checkout creates the worktree on factory/<id> from main and reuses it' {
        $a = New-ApprovedJob 'A'
        F checkout $a | Out-Null
        git -C $env:FACTORY_WORKTREE branch --show-current | Should -Be "factory/$a"
        $b = New-ApprovedJob 'B'
        F checkout $b | Out-Null
        git -C $env:FACTORY_WORKTREE branch --show-current | Should -Be "factory/$b"
        git -C $script:Repo branch --show-current | Should -Be 'main'
    }

    It 'status -Json prints the board' {
        New-BacklogJob | Out-Null
        (F status -Json | ConvertFrom-Json).jobs[0].id | Should -Be 'J-001'
    }
}

}

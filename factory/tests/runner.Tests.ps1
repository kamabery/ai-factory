#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

BeforeAll {
    $script:Scripts = Join-Path $PSScriptRoot '..\scripts' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Factory = Join-Path $script:Scripts 'factory.ps1'
    $script:Runner = Join-Path $script:Scripts 'runner.ps1'
    $script:Hook = Join-Path $script:Scripts 'hook-event.ps1'
    $script:Fixtures = Join-Path $PSScriptRoot 'fixtures'

    function F { & $script:Factory @args 2>$null }
    function Board { Get-Content (Join-Path $env:FACTORY_HOME 'board.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
    function Job([string]$id) { (Board).jobs | Where-Object { $_.id -eq $id } }
    function Events { Get-Content (Join-Path $env:FACTORY_HOME 'events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable -DateKind String } }
    function Run-Runner { & pwsh -NoProfile -File $script:Runner 2>&1 | Out-String }

    function New-ApprovedJob([string]$title, [string]$target = $script:Repo) {
        $idea = F new-idea "idea for $title" -Target $target
        $spec = Join-Path $TestDrive "spec-$([guid]::NewGuid().ToString('N')).md"
        Set-Content $spec "# Spec: $title`n`n## Target`n- Repo: $target`n`n## Gate`n- build: ``exit 0``"
        $id = F add-spec $idea $spec
        F approve $id | Out-Null
        Start-Sleep -Milliseconds 15
        $id
    }

    # A fake `claude -p "/factory run <id>"`: commits on the branch, drives the job to ready through
    # factory.ps1, logs the overlap check, and prints a claude-style JSON result.
    function Set-FakeClaude([string]$mode = 'complete') {
        $fake = Join-Path $TestDrive "fake-claude-$mode.ps1"
        @"
`$ErrorActionPreference = 'Stop'
`$id = `$env:FACTORY_JOB
`$f = '$script:Factory'
`$building = @((& `$f status -Json | ConvertFrom-Json).jobs | Where-Object { `$_.stage -in 'build','review','approve','gate' })
Add-Content (Join-Path `$env:FACTORY_HOME 'fake.log') "`$id building=`$(`$building.Count) cwd=`$((Get-Location).Path) branch=`$(git branch --show-current)"
`$runs = Join-Path `$env:FACTORY_HOME "jobs/`$id"
'{"type":"system","subtype":"init"}'
Start-Sleep -Milliseconds 1500
`$live = @(Get-ChildItem `$runs -Filter 'run-*.jsonl' | Where-Object { (Get-Content `$_.FullName -Raw) -match 'init' }).Count -gt 0
Add-Content (Join-Path `$env:FACTORY_HOME 'fake.log') "`$id live=`$live"
if ('$mode' -eq 'crash') { exit 1 }
if ('$mode' -eq 'stall') { '{"type":"result","total_cost_usd":0.1,"duration_ms":10}'; exit 0 }
Add-Content "`$id.txt" ([guid]::NewGuid())
git add -A 2>&1 | Out-Null; git commit -q -m "work `$id" 2>&1 | Out-Null
`$job = (& `$f status -Json | ConvertFrom-Json).jobs | Where-Object id -eq `$id
`$dir = Join-Path `$env:FACTORY_HOME "jobs/`$id/round-`$(`$job.round)"
New-Item -ItemType Directory -Force `$dir | Out-Null
Set-Content (Join-Path `$dir 'build.md') 'built'
& `$f set-stage `$id review | Out-Null
foreach (`$r in 'security','code','ux','ui') { Set-Content (Join-Path `$dir "review-`$r.md") 'Verdict: PASS' }
& `$f read-reviews `$id | Out-Null
Set-Content (Join-Path `$env:FACTORY_HOME "jobs/`$id/decision.md") 'Decision: APPROVE'
& `$f read-decision `$id | Out-Null
& `$f gate `$id | Out-Null
'{"type":"result","total_cost_usd":0.5,"duration_ms":2000}'
"@ | Set-Content $fake
        $env:FACTORY_CLAUDE_CMD = "& '$fake'"
    }
}

Describe 'runner.ps1 and hook-event.ps1' {

    BeforeEach {
        $script:Repo = Join-Path $TestDrive "repo-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory $script:Repo | Out-Null
        git -C $script:Repo init -q -b main 2>&1 | Out-Null
        git -C $script:Repo config user.email 'factory@test' | Out-Null
        git -C $script:Repo config user.name 'factory test' | Out-Null
        Set-Content (Join-Path $script:Repo 'README.md') 'fixture'
        git -C $script:Repo add -A 2>&1 | Out-Null
        git -C $script:Repo commit -q -m init 2>&1 | Out-Null

        $env:FACTORY_HOME = Join-Path $TestDrive "state-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $env:FACTORY_WORKTREE = "$script:Repo.wt"
        $env:FACTORY_GATE_CMDS = '[{"name":"build","cmd":"exit 0"}]'
        Push-Location $script:Repo
    }

    AfterEach {
        Pop-Location
        Remove-Item Env:FACTORY_HOME, Env:FACTORY_WORKTREE, Env:FACTORY_GATE_CMDS, Env:FACTORY_CLAUDE_CMD, Env:FACTORY_JOB, Env:FACTORY_IDEA -ErrorAction SilentlyContinue
    }

    Context 'runner' {
        It 'exits at once and does nothing while runner.lock is held by a live process' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            Set-Content (Join-Path $env:FACTORY_HOME 'runner.lock') $PID
            Run-Runner | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Job $id).stage | Should -Be 'approved'
        }

        It 'takes over a stale lock left by a dead process' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            Set-Content (Join-Path $env:FACTORY_HOME 'runner.lock') 999999
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
        }

        It 'drains two approved jobs strictly in order, reusing one worktree, and releases the lock' {
            $a = New-ApprovedJob 'A'
            $b = New-ApprovedJob 'B'
            Set-FakeClaude
            Run-Runner | Out-Null
            $LASTEXITCODE | Should -Be 0

            (Job $a).stage | Should -Be 'ready'
            (Job $b).stage | Should -Be 'ready'
            $log = @(Get-Content (Join-Path $env:FACTORY_HOME 'fake.log') | Where-Object { $_ -match 'building=' })
            $log.Count | Should -Be 2
            $log[0] | Should -Match "^$a building=1 "
            $log[1] | Should -Match "^$b building=1 "
            $log[0] | Should -Match "branch=factory/$a$"
            $log[1] | Should -Match "branch=factory/$b$"
            $log | ForEach-Object { $_ | Should -Match ([regex]::Escape("cwd=$env:FACTORY_WORKTREE")) }
            @(git worktree list).Count | Should -Be 2
            Test-Path (Join-Path $env:FACTORY_HOME 'runner.lock') | Should -BeFalse
        }

        It 'leaves the main checkout on a clean main' {
            New-ApprovedJob 'A' | Out-Null
            Set-FakeClaude
            Run-Runner | Out-Null
            git branch --show-current | Should -Be 'main'
            git status --porcelain | Should -BeNullOrEmpty
        }

        It 'streams claude output live to run-<n>.jsonl and records cost and duration from the result line' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            Run-Runner | Out-Null
            (Job $id).costUsd | Should -Be 0.5
            (Job $id).durationMs | Should -Be 2000
            $run = Get-Content (Join-Path $env:FACTORY_HOME "jobs/$id/run-1.jsonl")
            $run[0] | Should -Match '"subtype":"init"'
            $run[-1] | Should -Match '"type":"result"'
            Get-Content (Join-Path $env:FACTORY_HOME 'fake.log') | Should -Contain "$id live=True"
        }

        It "runs claude in the target's own worktree for a job on another repo" {
            Remove-Item Env:FACTORY_WORKTREE
            $target = Join-Path $TestDrive "target-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $id = New-ApprovedJob 'Elsewhere' $target
            Set-FakeClaude
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
            Get-Content (Join-Path $env:FACTORY_HOME 'fake.log') | Where-Object { $_ -match 'building=' } |
                Should -Match ([regex]::Escape("cwd=$target.wt") + " branch=factory/$id$")
            git -C $target branch --list "factory/$id" | Should -Not -BeNullOrEmpty
            git -C $script:Repo branch --list "factory/$id" | Should -BeNullOrEmpty
        }

        It 'writes headless-settings.json with the absolute factory.ps1 allow rule and the edit fence' {
            New-ApprovedJob 'A' | Out-Null
            Set-FakeClaude
            Run-Runner | Out-Null
            $settings = Get-Content (Join-Path $env:FACTORY_HOME 'headless-settings.json') -Raw | ConvertFrom-Json
            $abs = $script:Factory -replace '\\', '/'
            $settings.permissions.allow | Should -Contain "Bash(pwsh -NoProfile -File $abs *)"
            $state = ($env:FACTORY_HOME -replace '\\', '/') -replace '^([A-Za-z]):', { '/' + $_.Groups[1].Value.ToLowerInvariant() }
            $settings.permissions.deny | Should -Contain "Edit(/$state/jobs/*/spec.md)"
            $settings.permissions.deny | Should -Contain 'Edit(/factory/**)'
            $settings.permissions.deny | Should -Contain 'Edit(/.claude/**)'
        }

        It 'lets agents run the toolchain and read-only shell commands in either shell, and read the templates' {
            New-ApprovedJob 'A' | Out-Null
            Set-FakeClaude
            Run-Runner | Out-Null
            $p = (Get-Content (Join-Path $env:FACTORY_HOME 'headless-settings.json') -Raw | ConvertFrom-Json).permissions
            foreach ($rule in 'dotnet *', 'npm *', 'npx *', 'node *', 'git status*', 'ls*', 'cat *') {
                $p.allow | Should -Contain "Bash($rule)"
                $p.allow | Should -Contain "PowerShell($rule)"
            }
            $p.allow | Should -Contain 'PowerShell(Get-ChildItem*)'
            $p.deny | Should -Contain 'Bash(git push*)'
            $p.deny | Should -Contain 'Bash(git merge*)'
            $templates = (Join-Path (Split-Path $script:Scripts -Parent) 'templates') -replace '\\', '/'
            $p.additionalDirectories | Should -Contain $templates
        }

        It 'keeps non-ASCII text from claude intact in run-<n>.jsonl' {
            $id = New-ApprovedJob 'A'
            $fake = Join-Path $TestDrive 'fake-utf8.ps1'
            Set-Content $fake -Encoding utf8 @'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
'{"type":"assistant","text":"done — ✓ ⚠"}'
'{"type":"result","total_cost_usd":0.1,"duration_ms":10}'
'@
            $env:FACTORY_CLAUDE_CMD = "& '$fake'"
            # serve.ps1 starts the runner in a fresh hidden console, which is on the OEM codepage.
            & pwsh -NoProfile -Command "[Console]::OutputEncoding = [Text.Encoding]::GetEncoding(437); & '$script:Runner'" 2>&1 | Out-Null
            Get-Content (Join-Path $env:FACTORY_HOME "jobs/$id/run-1.jsonl") -Encoding utf8 | Should -Contain '{"type":"assistant","text":"done — ✓ ⚠"}'
        }

        It 'reuses the existing branch for a reworked job' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            Run-Runner | Out-Null
            $first = git rev-parse "factory/$id"
            F rework $id 'again' | Out-Null
            Run-Runner | Out-Null
            git rev-list --count "factory/$id" | Should -Be 3
            git merge-base --is-ancestor $first "factory/$id"; $LASTEXITCODE | Should -Be 0
            Test-Path (Join-Path $env:FACTORY_HOME "jobs/$id/run-2.jsonl") | Should -BeTrue
        }

        It 'escalates a job whose claude process exits non-zero' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude 'crash'
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
            (Job $id).flag.reason | Should -Be 'escalated'
            (Job $id).flag.detail | Should -Match 'exit 1'
        }

        It 'escalates a job left in Building, so the queue moves on' {
            $a = New-ApprovedJob 'A'
            $b = New-ApprovedJob 'B'
            Set-FakeClaude 'stall'
            Run-Runner | Out-Null
            (Job $a).flag.reason | Should -Be 'escalated'
            (Job $b).flag.reason | Should -Be 'escalated'
        }
    }

    Context 'spec-run' {
        BeforeAll { $script:SpecRun = Join-Path $script:Scripts 'spec-run.ps1' }

        It 'runs the spec command with FACTORY_IDEA set and streams run.jsonl' {
            $idea = F new-idea 'find books' -Target $script:Repo
            $fake = Join-Path $TestDrive 'fake-spec.ps1'
            @"
`$f = '$script:Factory'
`$s = Join-Path `$env:FACTORY_HOME "ideas/`$env:FACTORY_IDEA/spec-1.md"
Set-Content `$s '# Spec: Book search filter'
& `$f add-spec `$env:FACTORY_IDEA `$s | Out-Null
& `$f idea-done `$env:FACTORY_IDEA | Out-Null
'{"total_cost_usd":0.2,"duration_ms":900}'
"@ | Set-Content $fake
            $env:FACTORY_CLAUDE_CMD = "& '$fake'"
            & pwsh -NoProfile -File $script:SpecRun $idea | Out-Null
            $LASTEXITCODE | Should -Be 0
            ((Board).ideas | Where-Object id -eq $idea).status | Should -Be 'done'
            (Job 'J-001').title | Should -Be 'Book search filter'
            Get-Content (Join-Path $env:FACTORY_HOME "ideas/$idea/run.jsonl") | Should -Contain '{"total_cost_usd":0.2,"duration_ms":900}'
        }

        It 'marks the idea failed when the spec run leaves it drafting' {
            $idea = F new-idea 'vague' -Target $script:Repo
            $env:FACTORY_CLAUDE_CMD = 'exit 1'
            & pwsh -NoProfile -File $script:SpecRun $idea | Out-Null
            $i = (Board).ideas | Where-Object id -eq $idea
            $i.status | Should -Be 'failed'
            $i.reason | Should -Match 'exit 1'
        }
    }

    Context 'state resolution' {
        It 'resolves FACTORY_HOME from inside the worktree to the main checkout' {
            Remove-Item Env:FACTORY_HOME
            $env:FACTORY_HOME = $null
            & $script:Factory new-idea 'x' -Target $script:Repo 2>$null | Out-Null
            & $script:Factory add-spec I-001 (New-Item (Join-Path $TestDrive 's.md') -Value '# Spec: A' -Force) 2>$null | Out-Null
            & $script:Factory checkout J-001 2>$null | Out-Null
            Push-Location $env:FACTORY_WORKTREE
            try { $board = & $script:Factory status -Json 2>$null | ConvertFrom-Json } finally { Pop-Location }
            $board.jobs[0].id | Should -Be 'J-001'
            Test-Path (Join-Path $script:Repo '.factory-runs/board.json') | Should -BeTrue
            Test-Path (Join-Path $env:FACTORY_WORKTREE '.factory-runs') | Should -BeFalse
        }
    }

    Context 'hook-event' {
        It 'appends agent-start and agent-stop for FACTORY_JOB' {
            $id = New-ApprovedJob 'A'
            $env:FACTORY_JOB = $id
            Get-Content (Join-Path $script:Fixtures 'subagent-start.json') -Raw | & pwsh -NoProfile -File $script:Hook
            $LASTEXITCODE | Should -Be 0
            Get-Content (Join-Path $script:Fixtures 'subagent-stop.json') -Raw | & pwsh -NoProfile -File $script:Hook
            $e = @(Events | Where-Object { $_.kind -like 'agent-*' })
            $e.Count | Should -Be 2
            $e[0].kind | Should -Be 'agent-start'
            $e[0].agent | Should -Be 'security-reviewer'
            $e[0].job | Should -Be $id
            $e[0].round | Should -Be 1
            $e[1].kind | Should -Be 'agent-stop'
        }

        It "strips the plugin's factory: prefix and records the agent id and its transcript path" {
            $id = New-ApprovedJob 'A'
            $env:FACTORY_JOB = $id
            Get-Content (Join-Path $script:Fixtures 'subagent-start-plugin.json') -Raw | & pwsh -NoProfile -File $script:Hook
            $e = Events | Where-Object kind -eq 'agent-start'
            $e.agent | Should -Be 'builder'
            $e.agentId | Should -Be 'a2f500956fbe5654a'
            $e.transcript | Should -Be ([IO.Path]::GetFullPath('C:/Users/me/.claude/projects/x/00893aaf/subagents/agent-a2f500956fbe5654a.jsonl'))
        }

        It 'attributes to FACTORY_IDEA when there is no job' {
            F new-idea 'x' -Target $script:Repo | Out-Null
            $env:FACTORY_IDEA = 'I-001'
            Get-Content (Join-Path $script:Fixtures 'subagent-start.json') -Raw | & pwsh -NoProfile -File $script:Hook
            (Events | Where-Object kind -eq 'agent-start').idea | Should -Be 'I-001'
        }

        It 'exits 0 silently and appends nothing with no active job or idea' {
            F new-idea 'x' -Target $script:Repo | Out-Null
            $before = @(Events).Count
            $out = Get-Content (Join-Path $script:Fixtures 'subagent-start.json') -Raw | & pwsh -NoProfile -File $script:Hook 2>&1
            $LASTEXITCODE | Should -Be 0
            $out | Should -BeNullOrEmpty
            @(Events).Count | Should -Be $before
        }

        It 'ignores internal agents with an empty agent_type' {
            $id = New-ApprovedJob 'A'
            $env:FACTORY_JOB = $id
            $before = @(Events).Count
            Get-Content (Join-Path $script:Fixtures 'internal-stop.json') -Raw | & pwsh -NoProfile -File $script:Hook
            $LASTEXITCODE | Should -Be 0
            @(Events).Count | Should -Be $before
        }

        It 'exits 0 even on garbage input' {
            $env:FACTORY_JOB = 'J-001'
            'not json' | & pwsh -NoProfile -File $script:Hook 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
        }
    }
}

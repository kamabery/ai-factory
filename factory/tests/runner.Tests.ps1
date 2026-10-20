#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

BeforeAll {
    $script:Scripts = Join-Path $PSScriptRoot '..\scripts' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Factory = Join-Path $script:Scripts 'factory.ps1'
    $script:Runner = Join-Path $script:Scripts 'runner.ps1'
    $script:Hook = Join-Path $script:Scripts 'hook-event.ps1'
    $script:Fixtures = Join-Path $PSScriptRoot 'fixtures'
    . (Join-Path $script:Fixtures 'github.ps1')
    Start-GitHubStub

    function F { & $script:Factory @args 2>$null }
    function Board { Get-Content (Join-Path $env:FACTORY_HOME 'board.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
    function Job([string]$id) { (Board).jobs | Where-Object { $_.id -eq $id } }
    function Events { Get-Content (Join-Path $env:FACTORY_HOME 'events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable -DateKind String } }
    function Run-Runner { & pwsh -NoProfile -File $script:Runner 2>&1 | Out-String }

    function New-ApprovedJob([string]$title, [string]$target = $script:Repo) {
        $idea = F new-idea "idea for $title" -Target $script:Repo
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
git add -A 2>&1 | Out-Null; git -c user.email=f@t -c user.name=f commit -q -m "work `$id" 2>&1 | Out-Null
`$job = (& `$f status -Json | ConvertFrom-Json).jobs | Where-Object id -eq `$id
`$dir = Join-Path `$env:FACTORY_HOME "jobs/`$id/round-`$(`$job.round)"
New-Item -ItemType Directory -Force `$dir | Out-Null
Set-Content (Join-Path `$dir 'build.md') 'built'
& `$f set-stage `$id review | Out-Null
foreach (`$r in 'security','code','ux','ui') { Set-Content (Join-Path `$dir "review-`$r.md") 'Verdict: PASS' }
& `$f read-reviews `$id | Out-Null
if ('$mode' -eq 'deploy-stall') { '{"type":"result","total_cost_usd":0.1,"duration_ms":10}'; exit 0 }
Set-Content (Join-Path `$env:FACTORY_HOME "jobs/`$id/decision.md") 'Decision: APPROVE'
& `$f read-decision `$id | Out-Null
if ('$mode' -eq 'gate-crash') { exit 1 }
& `$f gate `$id | Out-Null
# FAKE_BAD_COST=<id>: a result line set-cost cannot parse, so finishing that job throws in the runner.
if (`$env:FAKE_BAD_COST -eq `$id) { '{"type":"result","total_cost_usd":"n/a","duration_ms":2000}' }
else { '{"type":"result","total_cost_usd":0.5,"duration_ms":2000}' }
"@ | Set-Content $fake
        $env:FACTORY_CLAUDE_CMD = "& '$fake'"
    }
}

AfterAll {
    Stop-GitHubStub
}

Describe 'runner.ps1 and hook-event.ps1' {

    BeforeEach {
        $script:Repo = "test/repo-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Remote $script:Repo

        $env:FACTORY_HOME = Join-Path $TestDrive "state-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $env:FACTORY_WORK = Join-Path $TestDrive "work-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $env:FACTORY_ENV_FILE = $script:EnvFile
        $env:FACTORY_GATE_CMDS = '[{"name":"build","cmd":"exit 0"}]'
    }

    AfterEach {
        Remove-Item Env:FACTORY_HOME, Env:FACTORY_WORK, Env:FACTORY_ENV_FILE, Env:FACTORY_GATE_CMDS, Env:FACTORY_CLAUDE_CMD, Env:FACTORY_JOB, Env:FACTORY_IDEA, Env:FAKE_BAD_COST -ErrorAction SilentlyContinue
    }

    Context 'runner log and resilience' {
        It 'logs start, each job, claude exit and result, the stage after, and exit drained' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            Run-Runner | Out-Null
            $log = Get-Content (Join-Path $env:FACTORY_HOME 'runner.log')
            $log[0] | Should -Match 'start pid=\d+'
            ($log -match "take $id$") | Should -Not -BeNullOrEmpty
            ($log | Where-Object { $_ -match "claude $id exit=0 result=yes$" }) | Should -Not -BeNullOrEmpty
            ($log | Where-Object { $_ -match "stage $id ready$" }) | Should -Not -BeNullOrEmpty
            $log[-1] | Should -Match 'exit drained$'
        }

        It 'an error finishing one job is logged with its stack, and the queue moves on' {
            $a = New-ApprovedJob 'A'
            $b = New-ApprovedJob 'B'
            Set-FakeClaude
            $env:FAKE_BAD_COST = $a
            Run-Runner | Out-Null
            (Job $b).stage | Should -Be 'ready'
            $log = Get-Content (Join-Path $env:FACTORY_HOME 'runner.log') -Raw
            $log | Should -Match "error $($a): "
            $log | Should -Match 'at .*runner\.ps1'
            $log | Should -Match 'exit drained'
            Test-Path (Join-Path $env:FACTORY_HOME 'runner.lock') | Should -BeFalse
        }
    }

    Context 'serve.ps1 watchdog' {
        It 'starts the runner for an approved job when no live runner holds the lock' {
            $id = New-ApprovedJob 'A'          # approved outside the dashboard: nobody starts a runner
            Set-FakeClaude
            Set-Content (Join-Path $env:FACTORY_HOME 'runner.lock') 999999   # a runner that died
            $port = Get-Random -Minimum 20000 -Maximum 40000
            $serve = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList @(
                '-NoProfile', '-File', (Join-Path $script:Scripts 'serve.ps1'),
                '-Port', $port, '-StateDir', $env:FACTORY_HOME, '-EnvFile', $script:EnvFile, '-WatchdogSeconds', 1)
            try {
                $deadline = (Get-Date).AddSeconds(60)
                while ((Get-Date) -lt $deadline -and (Job $id).stage -ne 'ready') { Start-Sleep -Milliseconds 500 }
                (Job $id).stage | Should -Be 'ready'
                ((Get-Content (Join-Path $env:FACTORY_HOME 'runner.log')) -match 'watchdog: started runner') | Should -Not -BeNullOrEmpty
            } finally {
                Stop-Process -Id $serve.Id -Force -ErrorAction SilentlyContinue
                $deadline = (Get-Date).AddSeconds(30)
                while ((Get-Date) -lt $deadline -and (Test-Path (Join-Path $env:FACTORY_HOME 'runner.lock'))) { Start-Sleep -Milliseconds 250 }
            }
        }
    }

    Context 'serve.ps1 PR sync' {
        It 'moves a ready job to done once the human merges its PR on GitHub' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            Run-Runner | Out-Null
            (Job $id).pr.number | Should -Not -BeNullOrEmpty
            Invoke-RestMethod -Method Put "http://localhost:$script:GhPort/repos/$script:Repo/pulls/$((Job $id).pr.number)/merge" `
                -Headers @{ Authorization = "Bearer $script:Token" } -ContentType 'application/json' -Body '{}' | Out-Null
            $port = Get-Random -Minimum 20000 -Maximum 40000
            $serve = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList @(
                '-NoProfile', '-File', (Join-Path $script:Scripts 'serve.ps1'),
                '-Port', $port, '-StateDir', $env:FACTORY_HOME, '-EnvFile', $script:EnvFile, '-PrSyncSeconds', 1)
            try {
                $deadline = (Get-Date).AddSeconds(60)
                while ((Get-Date) -lt $deadline -and (Job $id).stage -ne 'done') { Start-Sleep -Milliseconds 500 }
                (Job $id).stage | Should -Be 'done'
                (Job $id).merged.sha | Should -Be (RGit rev-parse main)
            } finally { Stop-Process -Id $serve.Id -Force -ErrorAction SilentlyContinue }
        }
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

        It 'keeps logging when a reader opens run-<n>.jsonl mid-run, as the dashboard''s Output tab does' {
            $id = New-ApprovedJob 'A'
            # Opens the log the way File.ReadAllBytes does (FileShare.Read) and holds it while claude
            # prints a line; the line must still reach the log and the job must not be escalated.
            $fake = Join-Path $TestDrive 'fake-claude-reader.ps1'
            @'
'{"type":"system","subtype":"init"}'
Start-Sleep -Milliseconds 300
$log = Join-Path $env:FACTORY_HOME "jobs/$env:FACTORY_JOB/run-1.jsonl"
$held = $null
try { $held = [IO.File]::Open($log, 'Open', 'Read', 'Read') } catch { }
'{"type":"assistant","note":"while read"}'
Start-Sleep -Milliseconds 500
if ($held) { $held.Dispose() }
'{"type":"result","total_cost_usd":0.1,"duration_ms":10}'
'@ | Set-Content $fake
            $env:FACTORY_CLAUDE_CMD = "& '$fake'"
            Run-Runner | Out-Null
            Get-Content (Join-Path $env:FACTORY_HOME 'runner.log') | Should -Not -Match "error $id"
            (Get-Content (Join-Path $env:FACTORY_HOME "jobs/$id/run-1.jsonl")) | Should -Contain '{"type":"assistant","note":"while read"}'
            (Job $id).flag.detail | Should -Not -Match 'runner error'
            (Job $id).costUsd | Should -Be 0.1
        }

        It 'takes over a stale lock left by a dead process' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            Set-Content (Join-Path $env:FACTORY_HOME 'runner.lock') 999999
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
        }

        It 'drains two approved jobs strictly in order, each in its own work dir, and releases the lock' {
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
            $log[0] | Should -Match ([regex]::Escape("cwd=$(Work $a) "))
            $log[1] | Should -Match ([regex]::Escape("cwd=$(Work $b) "))
            Test-Path (Join-Path $env:FACTORY_HOME 'runner.lock') | Should -BeFalse
        }

        It "publishes a job that lands in ready: its branch is pushed and a PR opened, main is untouched" {
            $main = RGit rev-parse main
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            Run-Runner | Out-Null
            $j = Job $id
            $j.stage | Should -Be 'ready'
            $j.pr.url | Should -Match '/pull/1$'
            RGit rev-parse "factory/$id" | Should -Be (git -C (Work $id) rev-parse HEAD)
            RGit rev-parse main | Should -Be $main
        }

        It 'publishes an escalated job too, so the human can read what it got to' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude 'stall'
            Run-Runner | Out-Null
            (Job $id).flag.reason | Should -Be 'escalated'
            (Job $id).pr | Should -Not -BeNullOrEmpty
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

        It "runs claude in the job's clone of its own target for a job on another repo" {
            $target = "test/target-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            New-Remote $target
            $id = New-ApprovedJob 'Elsewhere' $target
            Set-FakeClaude
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
            Get-Content (Join-Path $env:FACTORY_HOME 'fake.log') | Where-Object { $_ -match 'building=' } |
                Should -Match ([regex]::Escape("cwd=$(Work $id)") + " branch=factory/$id$")
            git -C (Bare $target) branch --list "factory/$id" | Should -Not -BeNullOrEmpty
            RGit branch --list "factory/$id" | Should -BeNullOrEmpty
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
            $envFile = (Join-Path (Split-Path (Split-Path $script:Scripts -Parent) -Parent) '.env') -replace '\\', '/' -replace '^([A-Za-z]):', { '/' + $_.Groups[1].Value.ToLowerInvariant() }
            $settings.permissions.deny | Should -Contain "Read(/$envFile)"
        }

        It 'lets agents run the toolchain and read-only shell commands in either shell, and read the templates' {
            New-ApprovedJob 'A' | Out-Null
            Set-FakeClaude
            Run-Runner | Out-Null
            $p = (Get-Content (Join-Path $env:FACTORY_HOME 'headless-settings.json') -Raw | ConvertFrom-Json).permissions
            foreach ($rule in 'dotnet *', 'npm *', 'npx *', 'node *', 'aspire-addon *', 'git status*', 'ls*', 'cat *') {
                $p.allow | Should -Contain "Bash($rule)"
                $p.allow | Should -Contain "PowerShell($rule)"
            }
            $p.allow | Should -Contain 'PowerShell(Get-ChildItem*)'
            # The devops agent's offline Terraform and the approver's smoke test - and no apply or plan.
            foreach ($rule in 'terraform init -backend=false*', 'terraform validate*', 'terraform test*', 'terraform fmt*', 'curl *') {
                $p.allow | Should -Contain "Bash($rule)"
            }
            @($p.allow | Where-Object { $_ -match "terraform (apply|plan|destroy)|terraform \*|\((az|gh) " }) | Should -BeNullOrEmpty
            $p.deny | Should -Contain 'Bash(git push*)'
            $p.deny | Should -Contain 'Bash(git merge*)'
            $templates = (Join-Path (Split-Path $script:Scripts -Parent) 'templates') -replace '\\', '/'
            $p.additionalDirectories | Should -Contain $templates
            $workspace = ([IO.Path]::GetFullPath((Join-Path $script:Scripts '..\..\..\template-projects'))) -replace '\\', '/'
            $p.additionalDirectories | Should -Contain $workspace
            $rule = ($workspace -replace '^([A-Za-z]):', { '/' + $_.Groups[1].Value.ToLowerInvariant() })
            $p.deny | Should -Contain "Edit(/$rule/**)"
        }

        It 'loads the factory plugin and the template workspace as plugins' {
            . (Join-Path $script:Scripts 'claude-run.ps1')
            $dirs = Get-PluginDirs
            $dirs | Should -Contain (Join-Path (Split-Path $script:Scripts -Parent) 'plugin')
            $dirs | Should -Contain ([IO.Path]::GetFullPath((Join-Path $script:Scripts '..\..\..\template-projects')))
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
            $first = RGit rev-parse "factory/$id"
            F rework $id 'again' | Out-Null
            Remove-Item -Recurse -Force (Work $id)    # a rework survives a lost work dir: the branch is on GitHub
            Run-Runner | Out-Null
            RGit rev-list --count "factory/$id" | Should -Be 3
            RGit merge-base --is-ancestor $first "factory/$id"; $LASTEXITCODE | Should -Be 0
            Test-Path (Join-Path $env:FACTORY_HOME "jobs/$id/run-2.jsonl") | Should -BeTrue
        }

        It 'escalates a job whose checkout fails with factory.ps1''s reason, in the flag and in runner.log' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            $bad = Join-Path $TestDrive 'no-token.env'
            Set-Content $bad (Get-Content $script:EnvFile | Where-Object { $_ -notmatch '^GITHUB_TOKEN=' })
            $env:FACTORY_ENV_FILE = $bad
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
            (Job $id).flag.reason | Should -Be 'escalated'
            (Job $id).flag.detail | Should -Match 'GITHUB_TOKEN is not set'
            (Get-Content (Join-Path $env:FACTORY_HOME 'runner.log') -Raw) | Should -Match "checkout $id failed: .*GITHUB_TOKEN is not set"
        }

        It 'escalates a job whose claude process exits non-zero' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude 'crash'
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
            (Job $id).flag.reason | Should -Be 'escalated'
            (Job $id).flag.detail | Should -Match 'exit 1'
        }

        It 'runs the gate itself when claude dies after the approver approved' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude 'gate-crash'
            Run-Runner | Out-Null
            $j = Job $id
            $j.stage | Should -Be 'ready'
            $j.flag | Should -BeNullOrEmpty
            $j.gate.build | Should -Be 0
            $j.gate.sha | Should -Be (git -C (Work $id) rev-parse "factory/$id")
        }

        It 'flags gate-failed, not escalated, when that gate is red' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude 'gate-crash'
            $env:FACTORY_GATE_CMDS = '[{"name":"build","cmd":"exit 1"}]'
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
            (Job $id).flag.reason | Should -Be 'gate-failed'
        }

        It 'compiles the lessons of a pass that leaves the job flagged' {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude 'stall'
            Run-Runner | Out-Null
            (Job $id).flag.reason | Should -Be 'escalated'
            $l = Get-Content (Join-Path $env:FACTORY_HOME "lessons/$id.json") -Raw | ConvertFrom-Json
            $l.flag | Should -Be 'escalated'
            $l.status | Should -Be 'open'
        }

        It 'escalates a job left in Building, so the queue moves on' {
            $a = New-ApprovedJob 'A'
            $b = New-ApprovedJob 'B'
            Set-FakeClaude 'stall'
            Run-Runner | Out-Null
            (Job $a).flag.reason | Should -Be 'escalated'
            (Job $b).flag.reason | Should -Be 'escalated'
        }

        It 'escalates a job claude leaves in deploy, the way it does any Building stage' {
            Use-Deploys
            try {
                $id = New-ApprovedJob 'A'
                Set-FakeClaude 'deploy-stall'
                Run-Runner | Out-Null
                (Job $id).flag.reason | Should -Be 'escalated'
                ((Job $id).history | Where-Object { $_.from -eq 'deploy' }).to | Should -Be 'ready'
            } finally { Clear-Deploys }
        }
    }

    Context 'providers' {
        BeforeEach {
            # A fake claude that records the provider environment it was started with, one line per job.
            $fake = Join-Path $TestDrive 'fake-claude-env.ps1'
            @'
$names = 'ANTHROPIC_BASE_URL', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_API_KEY', 'ANTHROPIC_DEFAULT_SONNET_MODEL', 'CLAUDE_CODE_SUBAGENT_MODEL'
$seen = [ordered]@{ job = $env:FACTORY_JOB }
foreach ($n in $names) { $v = [Environment]::GetEnvironmentVariable($n); if ($null -ne $v) { $seen[$n] = $v } }
Add-Content (Join-Path $env:FACTORY_HOME 'env.log') ($seen | ConvertTo-Json -Compress)
'{"type":"result","total_cost_usd":0.1,"duration_ms":10}'
'@ | Set-Content $fake
            $env:FACTORY_CLAUDE_CMD = "& '$fake'"
            $env:ANTHROPIC_API_KEY = 'sk-ant-from-the-runner'
            $script:ProviderEnv = Join-Path $TestDrive "provider-$([guid]::NewGuid().ToString('N')).env"
            $env:FACTORY_ENV_FILE = $script:ProviderEnv
        }

        AfterEach { Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue }

        BeforeAll {
            function Seen([string]$id) {
                Get-Content (Join-Path $env:FACTORY_HOME 'env.log') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } | Where-Object job -eq $id
            }
        }

        It 'gives an openrouter job OpenRouter''s endpoint, key and model overrides, and the next subscription job none of them' {
            Set-Content $script:ProviderEnv ((Get-Content $script:EnvFile) + 'OPENROUTER_API_KEY="sk-or-test"' + 'ANTHROPIC_DEFAULT_SONNET_MODEL=anthropic/claude-sonnet-5-5')
            $a = New-ApprovedJob 'A'
            F set-provider $a openrouter | Out-Null
            $b = New-ApprovedJob 'B'
            Run-Runner | Out-Null

            $or = Seen $a
            $or.ANTHROPIC_BASE_URL | Should -Be 'https://openrouter.ai/api'
            $or.ANTHROPIC_AUTH_TOKEN | Should -Be 'sk-or-test'
            $or.ANTHROPIC_API_KEY | Should -Be ''
            $or.ANTHROPIC_DEFAULT_SONNET_MODEL | Should -Be 'anthropic/claude-sonnet-5-5'
            $or.Keys | Should -Not -Contain 'CLAUDE_CODE_SUBAGENT_MODEL'

            $sub = Seen $b
            $sub.Keys | Should -Not -Contain 'ANTHROPIC_BASE_URL'
            $sub.Keys | Should -Not -Contain 'ANTHROPIC_AUTH_TOKEN'
            $sub.Keys | Should -Not -Contain 'ANTHROPIC_DEFAULT_SONNET_MODEL'
            $sub.ANTHROPIC_API_KEY | Should -Be 'sk-ant-from-the-runner'

            (Events | Where-Object { $_.job -eq $a -and $_.kind -eq 'cost' }).provider | Should -Be 'openrouter'
            (Events | Where-Object { $_.job -eq $b -and $_.kind -eq 'cost' }).provider | Should -Be 'subscription'
        }

        It 'escalates an openrouter job without running claude when .env has no OPENROUTER_API_KEY' {
            Set-Content $script:ProviderEnv (Get-Content $script:EnvFile)
            $id = New-ApprovedJob 'A'
            F set-provider $id openrouter | Out-Null
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
            (Job $id).flag.reason | Should -Be 'escalated'
            (Job $id).flag.detail | Should -Be 'provider: no OPENROUTER_API_KEY in .env'
            Test-Path (Join-Path $env:FACTORY_HOME 'env.log') | Should -BeFalse
        }

        It 'does not let agents read the environment, which holds the key' {
            Set-Content $script:ProviderEnv (Get-Content $script:EnvFile)
            New-ApprovedJob 'A' | Out-Null
            Run-Runner | Out-Null
            $p = (Get-Content (Join-Path $env:FACTORY_HOME 'headless-settings.json') -Raw | ConvertFrom-Json).permissions
            $p.allow | Should -Not -Contain 'Bash(printenv*)'
            $p.allow | Should -Not -Contain 'PowerShell(printenv*)'
            foreach ($rule in 'printenv*', 'env*', 'Get-ChildItem env:*', 'Get-Item env:*', 'Get-Content env:*') {
                $p.deny | Should -Contain "Bash($rule)"
                $p.deny | Should -Contain "PowerShell($rule)"
            }
        }
    }

    Context 'state resolution' {
        It "gives claude FACTORY_HOME, so factory.ps1 in the work dir (its own repo) uses this factory's state" {
            $id = New-ApprovedJob 'A'
            Set-FakeClaude
            Run-Runner | Out-Null
            (Job $id).stage | Should -Be 'ready'
            Test-Path (Join-Path (Work $id) '.factory-runs') | Should -BeFalse
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

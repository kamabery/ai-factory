#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

# The idea-session endpoints of serve.ps1, against the Anthropic stub. Each describe drives one
# session from start to close, so its tests run in order.
# Ported (roadmap spec 08): run-all.ps1 -Tag Ported runs every block here with FACTORY_IMPL=ps and dotnet;
# Start-FactoryServe starts serve.ps1 or the .NET server accordingly.
BeforeAll {
    . (Join-Path $PSScriptRoot 'fixtures/portable.ps1')   # -WindowStyle off Windows
    . (Join-Path $PSScriptRoot 'fixtures/serve-process.ps1')   # Start-FactoryServe: serve.ps1, or the .NET server
    $script:Scripts = Join-Path $PSScriptRoot '..\scripts' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Stub = Join-Path $PSScriptRoot 'fixtures\anthropic-stub.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Key = 'sk-ant-test-' + [guid]::NewGuid().ToString('N')
    . (Join-Path $PSScriptRoot 'fixtures\github.ps1')
    Start-GitHubStub

    function Free-Port { $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p }
    function Wait-Http([string] $url) {
        $deadline = (Get-Date).AddSeconds(30)
        while ($true) {
            try { Invoke-WebRequest $url -TimeoutSec 1 -SkipHttpErrorCheck | Out-Null; return }
            catch { if ((Get-Date) -gt $deadline) { throw "$url did not start" }; Start-Sleep -Milliseconds 200 }
        }
    }
    function Wait-Until([scriptblock] $cond, [string] $what) {
        $deadline = (Get-Date).AddSeconds(45)
        while (-not (& $cond)) {
            if ((Get-Date) -gt $deadline) { throw "timed out waiting for $what" }
            Start-Sleep -Milliseconds 250
        }
    }
    function Json($o) { $o | ConvertTo-Json -Depth 30 }
    function Read-J([string] $p) { Get-Content $p -Raw | ConvertFrom-Json -AsHashtable -DateKind String }

    function Text([string] $t) { @{ type = 'text'; text = $t } }
    function ToolUse([string] $id, [string] $name, $in) { @{ type = 'tool_use'; id = $id; name = $name; input = $in } }
    function Reply([object[]] $content, [string] $stop = 'end_turn') { @{ content = $content; stop_reason = $stop; usage = @{ input_tokens = 1000; output_tokens = 100 } } }

    $script:StubPort = Free-Port
    $script:StubDir = Join-Path $TestDrive 'stub'
    New-Item -ItemType Directory -Force $script:StubDir | Out-Null
    $script:StubProc = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $script:Stub, '-Port', $script:StubPort, '-Dir', $script:StubDir -PassThru -WindowStyle Hidden
    Wait-Http "http://localhost:$script:StubPort/"

    function Set-Replies {
        $dir = Join-Path $TestDrive "replies-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $n = 0
        foreach ($r in $args) { $n++; Set-Content (Join-Path $dir ('{0:D3}.json' -f $n)) (Json $r) }
        Invoke-RestMethod -Method Post "http://localhost:$script:StubPort/__reset" -Body (@{ dir = $dir } | ConvertTo-Json) | Out-Null
        $script:ReplyDir = $dir
    }
    function Request([int] $n) { Read-J (Join-Path $script:ReplyDir ('requests/{0:D3}.json' -f $n)) }
    function Requests { @(Get-ChildItem (Join-Path $script:ReplyDir 'requests') -Filter '*.json' -ErrorAction SilentlyContinue).Count }

    # One serve.ps1 per describe, on its own state dir.
    function Start-Serve([string] $envFile) {
        $script:State = Join-Path $TestDrive "state-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Force $script:State | Out-Null
        $env:FACTORY_HOME = $script:State
        $env:FACTORY_WORK = Join-Path $TestDrive "work-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        $env:FACTORY_CLAUDE_CMD = 'exit 0'
        $env:FACTORY_GATE_CMDS = '[{"name":"build","cmd":"exit 0"}]'
        $script:Port = Free-Port
        $script:Serve = Start-FactoryServe -Port $script:Port -StateDir $script:State -EnvFile $envFile
        Wait-Http "http://localhost:$script:Port/"
    }
    # The whole tree: serve.ps1 starts the runner detached, and a runner (or its git) left running in
    # the job's work dir kept Pester from deleting TestDrive ("Framework failed", PR #7).
    function Stop-Serve {
        if (-not $script:Serve -or $script:Serve.HasExited) { return }
        if ($IsWindows) { & taskkill /T /F /PID $script:Serve.Id 2>&1 | Out-Null } else { Stop-Process -Id $script:Serve.Id -Force }
        $script:Serve.WaitForExit(10000) | Out-Null
    }
    function Api([string] $method, [string] $path, $body, [string] $type = 'application/json') {
        $b = if ($null -eq $body) { $null } elseif ($type -eq 'application/json') { $body | ConvertTo-Json -Depth 10 } else { $body }
        $r = Invoke-WebRequest -Method $method "http://localhost:$script:Port$path" -Body $b -ContentType $type -SkipHttpErrorCheck
        [pscustomobject]@{ Status = [int]$r.StatusCode; Body = ($r.Content | ConvertFrom-Json -AsHashtable) }
    }
    function Session([string] $id = 'S-001') { Read-J (Join-Path $script:State "sessions/$id/session.json") }
    function Chat([string] $id = 'S-001') { (Read-J (Join-Path $script:State "sessions/$id/chat.json")).messages }
    function Board { Read-J (Join-Path $script:State 'board.json') }
    function Idle([string] $id = 'S-001') { $s = Session $id; $s.status -in 'idle', 'error' -and -not (Test-Path (Join-Path $script:State "sessions/$id/turn.lock")) }

    function New-Repo {
        $r = "test/repo-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Remote $r
        $r
    }
    function SessionClone([string] $id = 'S-001') { Join-Path $env:FACTORY_WORK "sessions/$id" }
    function Spec-Md([string] $title, [string] $repo) { "# Spec: $title`n`n## Target`n- Repo: $repo`n`n## Gate`n- build: ``exit 0``" }
    $script:Ask = @{ questions = @(@{ question = 'Who uses it?'; header = 'Users'; multiSelect = $false
                                        options = @(@{ label = 'Just me'; description = 'one' }, @{ label = 'A team'; description = 'many' }) }) }

    $github = Get-Content $script:EnvFile   # Start-GitHubStub's settings; serve.ps1 hands this file to factory.ps1
    $script:EnvFile = Join-Path $TestDrive '.env'
    Set-Content $script:EnvFile (@("ANTHROPIC_API_KEY=$script:Key", "ANTHROPIC_BASE_URL=http://localhost:$script:StubPort") + $github)
}

AfterAll {
    Stop-Serve
    if ($script:StubProc -and -not $script:StubProc.HasExited) { Stop-Process -Id $script:StubProc.Id -Force }
    Stop-GitHubStub
    Remove-Item Env:FACTORY_HOME, Env:FACTORY_WORK, Env:FACTORY_ENV_FILE, Env:FACTORY_CLAUDE_CMD, Env:FACTORY_GATE_CMDS -ErrorAction SilentlyContinue
}

Describe 'serve.ps1 port' -Tag 'Ported' {
    It 'without -Port listens on a random free port and prints it, so two dashboards never collide' {
        $procs = @()
        try {
            $urls = foreach ($n in 1, 2) {
                $state = Join-Path $TestDrive "port-state-$n"
                New-Item -ItemType Directory -Force $state | Out-Null
                $out = Join-Path $TestDrive "port-out-$n.txt"
                $procs += Start-FactoryServe -StateDir $state -EnvFile $script:EnvFile -RedirectStandardOutput $out
                Wait-Until { (Test-Path $out) -and ((Get-Content $out -Raw) -match 'factory dashboard on (http://localhost:\d+/)') } "dashboard $n to print its url"
                [regex]::Match((Get-Content $out -Raw), 'factory dashboard on (http://localhost:\d+/)').Groups[1].Value
            }
            $urls[0] | Should -Not -Be 'http://localhost:4317/'
            $urls[0] | Should -Not -Be $urls[1]
            foreach ($u in $urls) { (Invoke-WebRequest $u -SkipHttpErrorCheck).StatusCode | Should -Be 200 }
        } finally { $procs | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } }
    }

    It 'refuses a second dashboard on the same state, naming the first one, and takes over a dead one''s lock' {
        $state = Join-Path $TestDrive 'lock-state'
        New-Item -ItemType Directory -Force $state | Out-Null
        $serve = { param($n) $out = Join-Path $TestDrive "lock-out-$n.txt"
            $p = Start-FactoryServe -StateDir $state -EnvFile $script:EnvFile -RedirectStandardOutput $out -RedirectStandardError "$out.err"
            @{ Proc = $p; Out = $out } }
        $url = { param($o) [regex]::Match("$(Get-Content $o -Raw)",'factory dashboard on (http://localhost:\d+/)').Groups[1].Value }
        $procs = @()
        try {
            $first = & $serve 1; $procs += $first.Proc
            Wait-Until { (Test-Path $first.Out) -and (& $url $first.Out) } 'the first dashboard to print its url'
            $firstUrl = & $url $first.Out
            (Get-Content (Join-Path $state 'serve.lock'))[0] | Should -Be "$($first.Proc.Id)"

            $second = & $serve 2; $procs += $second.Proc
            $second.Proc.WaitForExit(30000) | Should -BeTrue
            $second.Proc.ExitCode | Should -Not -Be 0
            ((Get-Content $second.Out -Raw) + (Get-Content "$($second.Out).err" -Raw)) | Should -Match ([regex]::Escape($firstUrl))

            Stop-Process -Id $first.Proc.Id -Force   # no finally runs: the lock is left behind
            $first.Proc.WaitForExit(10000) | Out-Null
            Test-Path (Join-Path $state 'serve.lock') | Should -BeTrue
            $third = & $serve 3; $procs += $third.Proc
            Wait-Until { (Test-Path $third.Out) -and (& $url $third.Out) } 'a dashboard to take over the dead lock'
            (Get-Content (Join-Path $state 'serve.lock'))[0] | Should -Be "$($third.Proc.Id)"
        } finally { $procs | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } }
    }
}

Describe 'serve.ps1 ideate config' -Tag 'Ported' {
    AfterEach { Stop-Serve }

    It 'is disabled without a key, and enabled with one, which it never returns' {
        Start-Serve (Join-Path $TestDrive 'missing.env')
        (Api GET '/api/ideate/config').Body.enabled | Should -BeFalse
        (Api POST '/api/sessions' @{ target = (New-Repo); text = 'x' }).Status | Should -Be 409
        Stop-Serve

        Start-Serve $script:EnvFile
        $r = Api GET '/api/ideate/config'
        $r.Body.enabled | Should -BeTrue
        $r.Body.model | Should -Be 'claude-opus-5-5'
        $r.Body.owner | Should -Be 'test'
        ($r.Body | ConvertTo-Json) | Should -Not -Match ([regex]::Escape($script:Key))
    }
}

Describe 'serve.ps1 evaluations' -Tag 'Ported' {
    AfterEach { Stop-Serve }

    It 'lists every evals/J-NNN.json, newest job first, and serves the markdown' {
        Start-Serve $script:EnvFile
        (Invoke-WebRequest "http://localhost:$script:Port/api/evals").Content | Should -Be '[]'

        $dir = Join-Path $script:State 'evals'
        New-Item -ItemType Directory -Force $dir | Out-Null
        foreach ($id in 'J-002', 'J-010', 'J-009') {
            Set-Content (Join-Path $dir "$id.json") (Json @{ job = $id; rating = 'good'; title = "job $id" })
            Set-Content (Join-Path $dir "$id.md") "Rating: good`n`n# Eval: $id"
        }
        Set-Content (Join-Path $dir 'notes.txt') 'not an eval'
        $r = Api GET '/api/evals'
        $r.Status | Should -Be 200
        @($r.Body | ForEach-Object job) | Should -Be @('J-010', 'J-009', 'J-002')
        $r.Body[0].title | Should -Be 'job J-010'
        (Invoke-WebRequest "http://localhost:$script:Port/state/evals/J-010.md").Content | Should -Match '# Eval: J-010'
    }

    It 'saves human feedback through factory.ps1 feedback, and refuses it without an evaluation' {
        Start-Serve $script:EnvFile
        $dir = Join-Path $script:State 'evals'
        New-Item -ItemType Directory -Force $dir | Out-Null
        Set-Content (Join-Path $dir 'J-003.json') (Json @{ job = 'J-003'; rating = 'good'; title = 'job J-003' })

        $r = Api PUT '/api/evals/J-003/feedback' "Rating: mixed`n`nThe filter works; the empty state is ugly." 'text/markdown'
        $r.Status | Should -Be 200
        $r.Body.ok | Should -BeTrue
        @((Api GET '/api/evals').Body)[0].human.rating | Should -Be 'mixed'
        (Invoke-WebRequest "http://localhost:$script:Port/state/evals/J-003.feedback.md").Content | Should -Match 'empty state is ugly'

        (Api PUT '/api/evals/J-004/feedback' "Rating: good" 'text/markdown').Status | Should -Be 409
        (Api PUT '/api/evals/J-003/feedback' "no rating" 'text/markdown').Status | Should -Be 409
    }
}

Describe 'serve.ps1 lessons' -Tag 'Ported' {
    AfterEach { Stop-Serve }

    It 'lists every lessons/J-NNN.json, newest job first, serves the markdown, and names the templates repo' {
        $envFile = Join-Path $TestDrive 'lessons.env'
        Set-Content $envFile ((Get-Content $script:EnvFile) + 'FACTORY_TEMPLATES_REPO=test/template-projects')
        Start-Serve $envFile
        (Invoke-WebRequest "http://localhost:$script:Port/api/lessons").Content | Should -Be '[]'

        $dir = Join-Path $script:State 'lessons'
        New-Item -ItemType Directory -Force $dir | Out-Null
        foreach ($id in 'J-002', 'J-011') {
            Set-Content (Join-Path $dir "$id.json") (Json @{ job = $id; title = "job $id"; status = 'open'; templateGaps = 1 })
            Set-Content (Join-Path $dir "$id.md") "# Lessons: $id"
        }
        @((Api GET '/api/lessons').Body | ForEach-Object job) | Should -Be @('J-011', 'J-002')
        (Invoke-WebRequest "http://localhost:$script:Port/state/lessons/J-011.md").Content | Should -Match '# Lessons: J-011'
        (Api GET '/api/ideate/config').Body.templatesRepo | Should -Be 'test/template-projects'
    }

    It 'marks a lesson through factory.ps1 lesson-mark, and refuses an unknown status' {
        Start-Serve $script:EnvFile
        $dir = Join-Path $script:State 'lessons'
        New-Item -ItemType Directory -Force $dir | Out-Null
        Set-Content (Join-Path $dir 'J-004.json') (Json @{ job = 'J-004'; title = 'job'; status = 'open'; session = $null })

        $r = Api POST '/api/lessons/J-004/mark' @{ status = 'proposed'; session = 'S-002' }
        $r.Status | Should -Be 200
        $l = Read-J (Join-Path $dir 'J-004.json')
        $l.status | Should -Be 'proposed'
        $l.session | Should -Be 'S-002'

        (Api POST '/api/lessons/J-004/mark' @{ status = 'shipped' }).Status | Should -Be 409
    }
}

Describe 'serve.ps1 previews' -Tag 'Ported' {
    AfterEach { Stop-Serve }

    It 'Tear down answers at once, and QA login refuses a job without a live preview' {
        Start-Serve $script:EnvFile
        $env:FACTORY_ENV_FILE = $script:EnvFile
        $factory = Join-Path $script:Scripts 'factory.ps1'
        $repo = New-Repo
        $idea = & $factory new-idea 'x' -Target $repo 2>$null
        $spec = Join-Path $TestDrive "spec-$([guid]::NewGuid().ToString('N')).md"
        Set-Content $spec (Spec-Md 'Thing' $repo)
        $id = & $factory add-spec $idea $spec 2>$null

        $r = Api POST "/api/jobs/$id/destroy" @{}
        $r.Status | Should -Be 200
        $r.Body.ok | Should -BeTrue

        $q = Api GET "/api/jobs/$id/qa-login"
        $q.Status | Should -Be 409
        $q.Body.message | Should -Match 'no live preview'
    }

    It 'Run canary answers at once, and refuses while a live canary holds canary.lock' {
        Start-Serve $script:EnvFile
        $r = Api POST '/api/canary' @{}
        $r.Status | Should -Be 200
        $r.Body.ok | Should -BeTrue

        Set-Content (Join-Path $script:State 'canary.lock') $PID
        try {
            $busy = Api POST '/api/canary' @{}
            $busy.Status | Should -Be 409
            $busy.Body.message | Should -Match 'already running'
        } finally { Remove-Item (Join-Path $script:State 'canary.lock') -ErrorAction SilentlyContinue }
    }
}

Describe 'serve.ps1 state files' -Tag 'Ported' {
    AfterEach { Stop-Serve }

    It 'serves a run log the runner is still writing' {
        Start-Serve $script:EnvFile
        $dir = Join-Path $script:State 'jobs/J-001'
        New-Item -ItemType Directory -Force $dir | Out-Null
        $log = Join-Path $dir 'run-1.jsonl'
        $w = [IO.FileStream]::new($log, 'Create', 'Write', 'ReadWrite')
        try {
            $b = [Text.Encoding]::UTF8.GetBytes("{`"type`":`"system`"}`n")
            $w.Write($b, 0, $b.Length); $w.Flush()
            $r = Invoke-WebRequest "http://localhost:$script:Port/state/jobs/J-001/run-1.jsonl" -SkipHttpErrorCheck
            [int]$r.StatusCode | Should -Be 200
            "$($r.Content)" | Should -Match '"type":"system"'
        } finally { $w.Dispose() }
    }
}

Describe 'serve.ps1 run mode' -Tag 'Ported' {
    AfterEach { Stop-Serve }

    It 'has no provider endpoint: the run mode decides the provider' {
        Start-Serve $script:EnvFile
        (Api POST '/api/jobs/J-001/provider' @{ provider = 'openrouter' }).Status | Should -Be 404
    }

    It 'reports local and subscription by default' {
        Start-Serve $script:EnvFile
        $r = Api GET '/api/ideate/config'
        $r.Body.mode | Should -Be 'local'
        $r.Body.provider | Should -Be 'subscription'
    }

    It 'in cloud mode, enables idea sessions on FACTORY_FOUNDRY_RESOURCE, not the API key' {
        $cloud = Join-Path $TestDrive 'cloud.env'
        Set-Content $cloud ((Get-Content $script:EnvFile | Where-Object { $_ -notmatch '^ANTHROPIC_API_KEY=' }) + 'FACTORY_MODE=cloud')
        Start-Serve $cloud
        $r = Api GET '/api/ideate/config'
        $r.Body.mode | Should -Be 'cloud'
        $r.Body.provider | Should -Be 'foundry'
        $r.Body.enabled | Should -BeFalse
        $s = Api POST '/api/sessions' @{ target = (New-Repo); text = 'x' }
        $s.Status | Should -Be 409
        $s.Body.message | Should -Match 'FACTORY_FOUNDRY_RESOURCE'
        Stop-Serve

        Add-Content $cloud 'FACTORY_FOUNDRY_RESOURCE=fdy-test'
        Start-Serve $cloud
        (Api GET '/api/ideate/config').Body.enabled | Should -BeTrue
    }
}

Describe 'serve.ps1 blocked jobs' -Tag 'Ported' {
    BeforeAll {
        # Deploys on, with the gh, az and canary stubs, so the runner a rework starts touches nothing real.
        Use-Deploys
        Use-Canary
        Start-Serve $env:FACTORY_ENV_FILE
        $script:Factory = Join-Path $script:Scripts 'factory.ps1'
        function Fx { & pwsh -NoProfile -File $script:Factory @args 2>$null }
        function New-BlockedJob([string] $title) {
            $repo = New-Repo
            $idea = Fx new-idea $title -Target $repo
            $spec = Join-Path $TestDrive "$title-spec.md"
            Set-Content $spec (Spec-Md $title $repo)
            $id = Fx add-spec $idea $spec
            Fx approve $id | Out-Null
            Fx set-stage $id build -By runner | Out-Null
            Fx set-stage $id ready -Reason escalated -Detail 'gave up' | Out-Null
            $id
        }
    }
    AfterAll { Stop-Serve; Clear-Deploys }

    It 'Retry deploy calls factory.ps1 redeploy, which refuses a job blocked from build' {
        $id = New-BlockedJob 'retry'
        ((Board).jobs | Where-Object id -eq $id).stage | Should -Be 'blocked'
        $r = Api POST "/api/jobs/$id/redeploy" @{}
        $r.Status | Should -Be 409
        $r.Body.message | Should -Match 'blocked from build, not deploy'
    }

    It 'Resume calls factory.ps1 resume, which refuses a job not flagged usage-limit' {
        $id = New-BlockedJob 'resume'
        $r = Api POST "/api/jobs/$id/resume" @{}
        $r.Status | Should -Be 409
        $r.Body.message | Should -Match 'not flagged usage-limit'
    }

    It 'Rework on a blocked card calls factory.ps1 rework, and needs a note' {
        $id = New-BlockedJob 'rework'
        $r = Api POST "/api/jobs/$id/move" @{ to = 'approved' }
        $r.Status | Should -Be 409
        $r.Body.message | Should -Match 'needs a note'
        $r = Api POST "/api/jobs/$id/move" @{ to = 'approved'; note = 'remove the stray file' }
        $r.Status | Should -Be 200
        $r.Body.ok | Should -BeTrue
        Get-Content (Join-Path $script:State "jobs/$id/rework-2.md") | Should -Be 'remove the stray file'
        ((Board).jobs | Where-Object id -eq $id).history | Where-Object { $_.from -eq 'blocked' } | ForEach-Object to | Should -Be 'approved'
    }
}

Describe 'serve.ps1 idea session on an existing repo' -Tag 'Ported' {
    BeforeAll {
        Start-Serve $script:EnvFile
        $script:Target = New-Repo
        Set-Replies (Reply @((Text 'Hi.'), (ToolUse 'q1' 'ask_questions' $script:Ask)) 'tool_use') `
                    (Reply @((ToolUse 'b1' 'submit_brief' @{ markdown = "# Brief: Todos`n`n## Target`n- Repo: $script:Target" })) 'tool_use') `
                    (Reply @((ToolUse 's1' 'submit_specs' @{ specs = @(@{ k = 1; markdown = (Spec-Md 'Add a todo' $script:Target) }, @{ k = 2; markdown = (Spec-Md 'Check one off' $script:Target) }) })) 'tool_use') `
                    (Reply @((Text 'Two specs are ready.')))
    }
    AfterAll { Stop-Serve }

    It 'refuses a target that new-idea would refuse, and creates no session' {
        $r = Api POST '/api/sessions' @{ target = (Join-Path $TestDrive 'loose'); text = 'a todo app' }
        $r.Status | Should -Be 409
        $r.Body.message | Should -Match 'owner/name'
        Test-Path (Join-Path $script:State 'sessions') | Should -BeFalse
    }

    It 'creates S-001 for the idea and starts a turn that ends on the questions' {
        $r = Api POST '/api/sessions' @{ target = $script:Target; text = 'a todo app' }
        $r.Status | Should -Be 200
        $r.Body.message | Should -Be 'S-001'
        Wait-Until { (Idle) -and (Session).pending } 'the first turn'
        $s = Session
        $s.newTarget | Should -BeFalse
        $s.checkout | Should -Be (SessionClone)
        Get-Content (Join-Path (SessionClone) 'README.md') | Should -Be 'fixture'
        $s.pending.name | Should -Be 'ask_questions'
        (Chat)[0].content[0].text | Should -Match 'existing git repo'
        (Chat)[0].content[0].text | Should -Match 'a todo app'
        $raw = (Invoke-WebRequest "http://localhost:$script:Port/api/sessions").Content
        $raw | Should -Match '^\[\{'
        @((Api GET '/api/sessions').Body)[0].id | Should -Be 'S-001'
    }

    It 'refuses a reply while a turn holds the lock' {
        $lock = Join-Path $script:State 'sessions/S-001/turn.lock'
        Set-Content $lock $PID
        try { (Api POST '/api/sessions/S-001/reply' @{ text = 'x' }).Status | Should -Be 409 } finally { Remove-Item $lock }
    }

    It 'a reply answers the pending tool and starts the next turn' {
        (Api POST '/api/sessions/S-001/reply' @{ text = 'Who uses it? -> Just me' }).Status | Should -Be 200
        Wait-Until { (Idle) -and (Session).pending.name -eq 'submit_brief' } 'the brief'
        $sent = (Request 2).messages[-1].content
        $sent[-1].type | Should -Be 'tool_result'
        $sent[-1].tool_use_id | Should -Be 'q1'
        $sent[-1].content | Should -Be 'Who uses it? -> Just me'
    }

    It 'records the human''s exact words in human.jsonl, in order, each with what it answered' {
        $lines = @(Get-Content (Join-Path $script:State 'sessions/S-001/human.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
        $lines.Count | Should -Be 2
        $lines[0].kind | Should -Be 'idea'
        $lines[0].text | Should -Be 'a todo app'
        $lines[0].target | Should -Be $script:Target
        $lines[0].newTarget | Should -BeFalse
        $lines[1].kind | Should -Be 'answer'
        $lines[1].text | Should -Be 'Who uses it? -> Just me'
        $lines[1].asked.name | Should -Be 'ask_questions'
        $lines[1].asked.input.questions[0].question | Should -Be 'Who uses it?'
        $lines | ForEach-Object { $_.ts | Should -Not -BeNullOrEmpty }
    }

    It 'confirming the brief runs new-idea, writes brief.md and starts the spec turn' {
        $r = Api POST '/api/sessions/S-001/brief' $null
        $r.Status | Should -Be 200
        $r.Body.message | Should -Be 'I-001'
        $i = (Board).ideas[0]
        $i.status | Should -Be 'drafting'
        $i.target | Should -Be $script:Target
        $i.text | Should -Be 'Todos'
        Get-Content (Join-Path $script:State 'ideas/I-001/brief.md') -Raw | Should -Match '# Brief: Todos'
        Wait-Until { (Idle) -and @((Session).specs).Count -eq 2 } 'the specs'
        (Session).idea | Should -Be 'I-001'
        (Request 3).messages[-1].content[-1].content | Should -Match 'submit_specs'
    }

    It 'a direct edit rewrites an undecided spec' {
        $r = Api PUT '/api/sessions/S-001/specs/2' (Spec-Md 'Check a todo off' $script:Target) 'text/markdown'
        $r.Status | Should -Be 200
        Get-Content (Join-Path $script:State 'ideas/I-001/spec-2.md') -Raw | Should -Match 'Check a todo off'
        (Session).specs[1].title | Should -Be 'Check a todo off'
    }

    It 'Approve puts a spec on the board approved, Not yet leaves it in Backlog, and a decided spec cannot be decided again' {
        $r = Api POST '/api/sessions/S-001/specs/1' @{ decision = 'approved' }
        $r.Status | Should -Be 200
        $r.Body.message | Should -Be 'J-001'
        ((Board).jobs | Where-Object id -eq 'J-001').approvedAt | Should -Not -BeNullOrEmpty
        (Api POST '/api/sessions/S-001/specs/2' @{ decision = 'backlog' }).Body.message | Should -Be 'J-002'
        ((Board).jobs | Where-Object id -eq 'J-002').stage | Should -Be 'backlog'
        (Api POST '/api/sessions/S-001/specs/1' @{ decision = 'rejected' }).Status | Should -Be 409
        $specs = (Session).specs
        $specs[0].decision | Should -Be 'approved'
        $specs[0].job | Should -Be 'J-001'
        $specs[1].decision | Should -Be 'backlog'
    }

    It 'close marks the idea done and the session closed, and hides it from the list' {
        (Api POST '/api/sessions/S-001/close' @{ failed = $false }).Status | Should -Be 200
        ((Board).ideas | Where-Object id -eq 'I-001').status | Should -Be 'done'
        (Session).status | Should -Be 'closed'
        @((Api GET '/api/sessions').Body).Count | Should -Be 0
        Test-Path (SessionClone) | Should -BeFalse
    }
}

Describe 'serve.ps1 idea session on an unmerged base' -Tag 'Ported' {
    BeforeAll {
        Start-Serve $script:EnvFile
        $env:FACTORY_ENV_FILE = $script:EnvFile
        $factory = Join-Path $script:Scripts 'factory.ps1'
        function Fx { & pwsh -NoProfile -File $factory @args 2>$null }
        $script:Target = New-Repo
        # J-001: in ready with its PR open, the base the session builds on.
        $spec = Join-Path $TestDrive "base-$([guid]::NewGuid().ToString('N').Substring(0, 8)).md"
        Set-Content $spec (Spec-Md 'Tags' $script:Target)
        $idea = Fx new-idea 'tags' -Target $script:Target
        $script:BaseJob = Fx add-spec $idea $spec
        Fx approve $script:BaseJob | Out-Null
        $wt = Fx checkout $script:BaseJob
        Set-Content (Join-Path $wt 'tags.txt') 'the tag list'
        git -C $wt add -A 2>&1 | Out-Null
        git -C $wt -c user.email=f@t -c user.name=f commit -q -m tags 2>&1 | Out-Null
        Fx set-stage $script:BaseJob build -By runner | Out-Null
        Fx set-stage $script:BaseJob ready -Reason escalated -Detail x | Out-Null
        Fx publish $script:BaseJob | Out-Null
        Set-Replies (Reply @((ToolUse 'q1' 'ask_questions' $script:Ask)) 'tool_use') `
                    (Reply @((ToolUse 'b1' 'submit_brief' @{ markdown = "# Brief: Tags in the menu`n`n## Target`n- Repo: $script:Target" })) 'tool_use') `
                    (Reply @((ToolUse 's1' 'submit_specs' @{ specs = @(@{ k = 1; markdown = (Spec-Md 'Menu' $script:Target) }) })) 'tool_use') `
                    (Reply @((Text 'One spec.')))
    }
    AfterAll { Stop-Serve; Remove-Item Env:FACTORY_ENV_FILE -ErrorAction SilentlyContinue }

    It 'refuses a base that is not an open job on the target, and creates no session' {
        $r = Api POST '/api/sessions' @{ target = $script:Target; text = 'menu'; base = 'J-999' }
        $r.Status | Should -Be 409
        $r.Body.message | Should -Match 'base refused'
        Test-Path (Join-Path $script:State 'sessions') | Should -BeFalse
    }

    It 'clones the base branch and tells the model it reads unmerged work' {
        $r = Api POST '/api/sessions' @{ target = $script:Target; text = 'move tag list to the main menu'; base = $script:BaseJob }
        $r.Status | Should -Be 200
        Wait-Until { (Idle) -and (Session).pending } 'the first turn'
        (Session).base | Should -Be $script:BaseJob
        Get-Content (Join-Path (SessionClone) 'tags.txt') | Should -Be 'the tag list'
        (Chat)[0].content[0].text | Should -Match "factory/$script:BaseJob"
    }

    It 'confirming the brief makes an idea on the base, and its approved spec is a job stacked on it' {
        (Api POST '/api/sessions/S-001/reply' @{ text = 'Just me' }).Status | Should -Be 200
        Wait-Until { (Idle) -and (Session).pending.name -eq 'submit_brief' } 'the brief'
        $r = Api POST '/api/sessions/S-001/brief' $null
        $r.Status | Should -Be 200
        ((Board).ideas | Where-Object id -eq $r.Body.message).base | Should -Be $script:BaseJob
        Wait-Until { (Idle) -and @((Session).specs).Count -eq 1 } 'the specs'
        $job = (Api POST '/api/sessions/S-001/specs/1' @{ decision = 'backlog' }).Body.message
        ((Board).jobs | Where-Object id -eq $job).base | Should -Be $script:BaseJob
    }

    It 'Abandon is refused while a turn runs, then closes the session and abandons its idea' {
        $lock = Join-Path $script:State 'sessions/S-001/turn.lock'
        Set-Content $lock $PID
        try { (Api POST '/api/sessions/S-001/close' @{ abandon = $true }).Status | Should -Be 409 } finally { Remove-Item $lock }
        (Api POST '/api/sessions/S-001/close' @{ abandon = $true }).Status | Should -Be 200
        (Session).status | Should -Be 'closed'
        ((Board).ideas | Where-Object id -eq (Session).idea).status | Should -Be 'abandoned'
        @((Api GET '/api/sessions').Body).Count | Should -Be 0
    }

    It 'Abandon on an idea card abandons it through factory.ps1, and refuses one that is done' {
        $i = Fx new-idea 'left over' -Target $script:Target
        (Api POST "/api/ideas/$i/abandon" $null).Status | Should -Be 200
        ((Board).ideas | Where-Object id -eq $i).status | Should -Be 'abandoned'
        (Api POST "/api/ideas/$i/abandon" $null).Status | Should -Be 409
    }
}

Describe 'serve.ps1 repo list and Add new' -Tag 'Ported' {
    BeforeAll {
        Start-Serve $script:EnvFile
        $script:Existing = New-Repo
        $script:Picked = "new-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Set-Replies (Reply @((ToolUse 'q1' 'ask_questions' $script:Ask)) 'tool_use') `
                    (Reply @((ToolUse 'p1' 'propose_scaffold' @{ repo = $script:Picked; name = 'todo'; resource = 'Todo'; db = 'sqlite'; ui = 'react'; events = $false })) 'tool_use') `
                    (Reply @((ToolUse 'b1' 'submit_brief' @{ markdown = "# Brief: A todo app`n`n## Target`n- Repo: test/$script:Picked" })) 'tool_use')
    }
    AfterAll { Stop-Serve }

    It 'GET /api/repos lists the token user''s GitHub repos through factory.ps1 list-repos' {
        $r = Api GET '/api/repos'
        $r.Status | Should -Be 200
        @($r.Body) | Should -Contain $script:Existing
    }

    It 'Add new starts a session with no target, no clone and no check-target, and asks for a repo name' {
        # An earlier test's runner may still be publishing its PR (slower on the azure state backend);
        # its requests are not this session's. Only this test's runner counts, not a live factory's.
        Wait-Until {
            $holder = (Get-Content (Join-Path $script:State 'runner.lock') -ErrorAction SilentlyContinue | Select-Object -First 1) -as [int]
            -not ($holder -and (Get-Process -Id $holder -ErrorAction SilentlyContinue))
        } 'earlier runners to finish'
        $before = @(Get-Content (Join-Path $script:GitBase 'requests.jsonl')).Count
        $r = Api POST '/api/sessions' @{ new = $true; text = 'a todo app' }
        $r.Status | Should -Be 200
        Wait-Until { (Idle) -and (Session).pending } 'the first turn'
        $s = Session
        $s.target | Should -BeNullOrEmpty
        $s.newTarget | Should -BeTrue
        $s.checkout | Should -BeNullOrEmpty
        @(Get-Content (Join-Path $script:GitBase 'requests.jsonl')).Count | Should -Be $before
        (Chat)[0].content[0].text | Should -Match 'no repo yet'
        (Chat)[0].content[0].text | Should -Match '\btest\b'
        @((Api GET '/api/sessions').Body)[0].target | Should -BeNullOrEmpty
    }

    It 'the grill runs until propose_scaffold names a repo' {
        (Api POST '/api/sessions/S-001/reply' @{ text = 'Who uses it? -> Just me' }).Status | Should -Be 200
        Wait-Until { (Idle) -and (Session).pending.name -eq 'propose_scaffold' } 'the scaffold proposal'
        (Session).pending.input.repo | Should -Be $script:Picked
    }

    It 'confirming the scaffold refuses a repo that already exists, and leaves the session as it was' {
        $name = ($script:Existing -split '/')[1]
        $r = Api POST '/api/sessions/S-001/reply' @{ text = 'ok'; scaffold = @{ repo = $name; name = 'todo'; resource = 'Todo'; db = 'sqlite'; ui = 'react'; events = $false } }
        $r.Status | Should -Be 409
        $r.Body.message | Should -Match 'pick it from the list'
        $s = Session
        $s.target | Should -BeNullOrEmpty
        $s.scaffold | Should -BeNullOrEmpty
        $s.pending.name | Should -Be 'propose_scaffold'
    }

    It 'confirming the scaffold sets the target from the owner and repo, and keeps repo out of the scaffold' {
        $r = Api POST '/api/sessions/S-001/reply' @{ text = 'Scaffold confirmed'; scaffold = @{ repo = $script:Picked; name = 'todo'; resource = 'Todo'; db = 'sqlite'; ui = 'react'; events = $false } }
        $r.Status | Should -Be 200
        Wait-Until { (Idle) -and (Session).pending.name -eq 'submit_brief' } 'the brief'
        $s = Session
        $s.target | Should -Be "test/$script:Picked"
        $s.scaffold.Contains('repo') | Should -BeFalse
        (Request 3).messages[-1].content[-1].content | Should -Match "test/$script:Picked"
    }

    It 'confirming the brief creates the idea and its bootstrap job on the chosen target' {
        $r = Api POST '/api/sessions/S-001/brief' $null
        $r.Status | Should -Be 200
        $r.Body.message | Should -Be 'I-001 J-001'
        (Board).ideas[0].target | Should -Be "test/$script:Picked"
        (Board).ideas[0].scaffold.Contains('repo') | Should -BeFalse
    }
}

Describe 'serve.ps1 Add new without an owner' -Tag 'Ported' {
    AfterAll { Stop-Serve }

    It 'refuses Add new when GITHUB_OWNER is not set, and creates no session' {
        $env = Join-Path $TestDrive 'no-owner.env'
        Set-Content $env (Get-Content $script:EnvFile | Where-Object { $_ -notmatch '^GITHUB_OWNER=' })
        Start-Serve $env
        $r = Api POST '/api/sessions' @{ new = $true; text = 'a todo app' }
        $r.Status | Should -Be 409
        $r.Body.message | Should -Match 'GITHUB_OWNER'
        Test-Path (Join-Path $script:State 'sessions') | Should -BeFalse
    }
}

Describe 'serve.ps1 idea session on a new target' -Tag 'Ported' {
    BeforeAll {
        Start-Serve $script:EnvFile
        $script:NewTarget = "test/new-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Set-Replies (Reply @((ToolUse 'p1' 'propose_scaffold' @{ name = 'todo'; resource = 'Todo'; db = 'sqlite'; ui = 'react'; events = $false })) 'tool_use') `
                    (Reply @((ToolUse 'b1' 'submit_brief' @{ markdown = "# Brief: A todo app`n`n## Target`n- Repo: $script:NewTarget" })) 'tool_use') `
                    (Reply @((Text 'Reading the scaffold.')))
    }
    AfterAll { Stop-Serve }

    It 'says the target is new and ends on the scaffold proposal' {
        (Api POST '/api/sessions' @{ target = $script:NewTarget; text = 'a todo app' }).Status | Should -Be 200
        Wait-Until { (Idle) -and (Session).pending } 'the scaffold proposal'
        (Session).newTarget | Should -BeTrue
        (Session).checkout | Should -BeNullOrEmpty
        (Session).pending.name | Should -Be 'propose_scaffold'
        (Chat)[0].content[0].text | Should -Match 'propose_scaffold'
    }

    It 'the confirmed scaffold is stored with the reply' {
        (Api POST '/api/sessions/S-001/reply' @{ text = 'Confirmed: todo / Todo / sqlite / react / no events'; scaffold = @{ name = 'todo'; resource = 'Todo'; db = 'sqlite'; ui = 'react'; events = $false } }).Status | Should -Be 200
        Wait-Until { (Idle) -and (Session).pending.name -eq 'submit_brief' } 'the brief'
        (Session).scaffold.resource | Should -Be 'Todo'
    }

    It 'confirming the brief creates the waiting idea and its bootstrap job, and starts no turn' {
        $r = Api POST '/api/sessions/S-001/brief' $null
        $r.Status | Should -Be 200
        $r.Body.message | Should -Be 'I-001 J-001'
        ((Board).jobs | Where-Object id -eq 'J-001').kind | Should -Be 'bootstrap'
        $s = Session
        $s.bootstrap | Should -Be 'J-001'
        $s.pending | Should -BeNullOrEmpty
        Start-Sleep -Seconds 2
        (Requests) | Should -Be 2
        (Chat)[-1].content[-1].content | Should -Match 'J-001'
    }

    It 'Write specs is refused while the idea waits for its bootstrap merge' {
        $r = Api POST '/api/sessions/S-001/write-specs' $null
        $r.Status | Should -Be 409
        $r.Body.message | Should -Match 'waiting'
    }

    It 'after the bootstrap merge, Write specs clones the scaffolded target for the spec turn' {
        $env:FACTORY_ENV_FILE = $script:EnvFile
        $f = Join-Path $script:Scripts 'factory.ps1'
        & $f approve J-001 2>$null | Out-Null
        & $f set-stage J-001 build -By runner 2>$null | Out-Null
        $wt = & $f checkout J-001 2>$null
        Set-Content (Join-Path $wt 'todo.slnx') '<Solution />'
        git -C $wt add -A 2>&1 | Out-Null
        git -C $wt -c user.email=f@t -c user.name=f commit -q -m scaffold 2>&1 | Out-Null
        & $f set-stage J-001 ready -By runner -Reason escalated -Detail 'test' 2>$null | Out-Null
        & $f merge J-001 2>$null | Should -Contain 'released I-001'

        (Api POST '/api/sessions/S-001/write-specs' $null).Status | Should -Be 200
        Wait-Until { Idle } 'the spec turn'
        (Session).checkout | Should -Be (SessionClone)
        Test-Path (Join-Path (SessionClone) 'todo.slnx') | Should -BeTrue
    }
}

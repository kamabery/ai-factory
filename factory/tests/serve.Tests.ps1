#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

# The idea-session endpoints of serve.ps1, against the Anthropic stub. Each describe drives one
# session from start to close, so its tests run in order.
BeforeAll {
    $script:Scripts = Join-Path $PSScriptRoot '..\scripts' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Stub = Join-Path $PSScriptRoot 'fixtures\anthropic-stub.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Key = 'sk-ant-test-' + [guid]::NewGuid().ToString('N')

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
        $env:FACTORY_CLAUDE_CMD = 'exit 0'
        $env:FACTORY_GATE_CMDS = '[{"name":"build","cmd":"exit 0"}]'
        $script:Port = Free-Port
        $script:Serve = Start-Process pwsh -ArgumentList '-NoProfile', '-File', (Join-Path $script:Scripts 'serve.ps1'), '-Port', $script:Port,
            '-StateDir', $script:State, '-EnvFile', $envFile -PassThru -WindowStyle Hidden
        Wait-Http "http://localhost:$script:Port/"
    }
    function Stop-Serve { if ($script:Serve -and -not $script:Serve.HasExited) { Stop-Process -Id $script:Serve.Id -Force } }
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
        $r = Join-Path $TestDrive "repo-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Force $r | Out-Null
        git -C $r init -q -b main 2>&1 | Out-Null
        Set-Content (Join-Path $r 'README.md') 'target readme'
        git -C $r add -A 2>&1 | Out-Null
        git -C $r -c user.email=t@t -c user.name=t commit -q -m init 2>&1 | Out-Null
        $r
    }
    function Spec-Md([string] $title, [string] $repo) { "# Spec: $title`n`n## Target`n- Repo: $repo`n`n## Gate`n- build: ``exit 0``" }
    $script:Ask = @{ questions = @(@{ question = 'Who uses it?'; header = 'Users'; multiSelect = $false
                                        options = @(@{ label = 'Just me'; description = 'one' }, @{ label = 'A team'; description = 'many' }) }) }

    $script:EnvFile = Join-Path $TestDrive '.env'
    Set-Content $script:EnvFile "ANTHROPIC_API_KEY=$script:Key`nANTHROPIC_BASE_URL=http://localhost:$script:StubPort"
}

AfterAll {
    Stop-Serve
    if ($script:StubProc -and -not $script:StubProc.HasExited) { Stop-Process -Id $script:StubProc.Id -Force }
    Remove-Item Env:FACTORY_HOME, Env:FACTORY_CLAUDE_CMD, Env:FACTORY_GATE_CMDS -ErrorAction SilentlyContinue
}

Describe 'serve.ps1 ideate config' {
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
        ($r.Body | ConvertTo-Json) | Should -Not -Match ([regex]::Escape($script:Key))
    }
}

Describe 'serve.ps1 evaluations' {
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

Describe 'serve.ps1 idea session on an existing repo' {
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
        $bad = Join-Path $TestDrive 'loose'
        New-Item -ItemType Directory -Force $bad | Out-Null
        Set-Content (Join-Path $bad 'x.txt') 'x'
        $r = Api POST '/api/sessions' @{ target = $bad; text = 'a todo app' }
        $r.Status | Should -Be 409
        $r.Body.message | Should -Match 'not a git repo'
        Test-Path (Join-Path $script:State 'sessions') | Should -BeFalse
    }

    It 'creates S-001 for the idea and starts a turn that ends on the questions' {
        $r = Api POST '/api/sessions' @{ target = $script:Target; text = 'a todo app' }
        $r.Status | Should -Be 200
        $r.Body.message | Should -Be 'S-001'
        Wait-Until { (Idle) -and (Session).pending } 'the first turn'
        $s = Session
        $s.newTarget | Should -BeFalse
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
    }
}

Describe 'serve.ps1 idea session on a new target' {
    BeforeAll {
        Start-Serve $script:EnvFile
        $script:NewTarget = Join-Path $TestDrive "new-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Set-Replies (Reply @((ToolUse 'p1' 'propose_scaffold' @{ name = 'todo'; resource = 'Todo'; db = 'sqlite'; ui = 'react'; events = $false })) 'tool_use') `
                    (Reply @((ToolUse 'b1' 'submit_brief' @{ markdown = "# Brief: A todo app`n`n## Target`n- Repo: $script:NewTarget" })) 'tool_use')
    }
    AfterAll { Stop-Serve }

    It 'says the target is new and ends on the scaffold proposal' {
        (Api POST '/api/sessions' @{ target = $script:NewTarget; text = 'a todo app' }).Status | Should -Be 200
        Wait-Until { (Idle) -and (Session).pending } 'the scaffold proposal'
        (Session).newTarget | Should -BeTrue
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
}

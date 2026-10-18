#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

BeforeAll {
    $script:Turn = Join-Path $PSScriptRoot '..\scripts\idea-turn.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Stub = Join-Path $PSScriptRoot 'fixtures\anthropic-stub.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Key = 'sk-ant-test-' + [guid]::NewGuid().ToString('N')

    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $script:Port = $l.LocalEndpoint.Port; $l.Stop()
    $script:StubDir = Join-Path $TestDrive 'stub'
    New-Item -ItemType Directory -Force $script:StubDir | Out-Null
    $script:StubProc = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $script:Stub, '-Port', $script:Port, '-Dir', $script:StubDir -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(20)
    while ($true) {
        try { Invoke-WebRequest "http://localhost:$script:Port/" -TimeoutSec 1 | Out-Null; break }
        catch { if ((Get-Date) -gt $deadline) { throw 'stub did not start' }; Start-Sleep -Milliseconds 200 }
    }

    function Json($o) { $o | ConvertTo-Json -Depth 30 }
    function Read-J([string] $p) { Get-Content $p -Raw | ConvertFrom-Json -AsHashtable -DateKind String }

    # Scripts the stub's replies for one test: each argument is one response (a hashtable).
    function Set-Replies {
        $dir = Join-Path $TestDrive "replies-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $n = 0
        foreach ($r in $args) { $n++; Set-Content (Join-Path $dir ('{0:D3}.json' -f $n)) (Json $r) }
        Invoke-RestMethod -Method Post "http://localhost:$script:Port/__reset" -Body (@{ dir = $dir } | ConvertTo-Json) | Out-Null
        $script:ReplyDir = $dir
    }
    function Request([int] $n) { Read-J (Join-Path $script:ReplyDir ('requests/{0:D3}.json' -f $n)) }
    function Requests { @(Get-ChildItem (Join-Path $script:ReplyDir 'requests') -Filter '*.json' -ErrorAction SilentlyContinue).Count }

    function Text([string] $t) { @{ type = 'text'; text = $t } }
    function ToolUse([string] $id, [string] $name, $in) { @{ type = 'tool_use'; id = $id; name = $name; input = $in } }
    function Reply([object[]] $content, [string] $stop = 'end_turn', $usage = @{}) { @{ content = $content; stop_reason = $stop; usage = $usage } }
    $script:Ask = @{ questions = @(@{ question = 'Who uses it?'; header = 'Users'; multiSelect = $false
                                        options = @(@{ label = 'Just me'; description = 'one person' }, @{ label = 'A team'; description = 'several' }) }) }

    function New-Session([string] $target = $script:Target, [string] $idea = $null) {
        $dir = Join-Path $env:FACTORY_HOME 'sessions/S-001'
        New-Item -ItemType Directory -Force $dir | Out-Null
        Set-Content (Join-Path $dir 'session.json') (Json ([ordered]@{
            id = 'S-001'; target = $target; text = 'a todo app'; newTarget = $false; status = 'idle'; error = $null
            model = $null; costUsd = 0; scaffold = $null; idea = $idea; bootstrap = $null; pending = $null; pendingResults = @(); specs = @()
            createdAt = '2026-10-17T00:00:00Z' }))
        Set-Content (Join-Path $dir 'chat.json') (Json @{ messages = @(@{ role = 'user'; content = @(@{ type = 'text'; text = "Target: $target`n`nIdea: a todo app" }) }) })
        $dir
    }
    function Session { Read-J (Join-Path $env:FACTORY_HOME 'sessions/S-001/session.json') }
    function Chat { (Read-J (Join-Path $env:FACTORY_HOME 'sessions/S-001/chat.json')).messages }
    function Stream { Get-Content (Join-Path $env:FACTORY_HOME 'sessions/S-001/stream.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } }
    function Run-Turn([string] $envFile = $script:EnvFile) { & $script:Turn -Session S-001 -StateDir $env:FACTORY_HOME -EnvFile $envFile 6>$null 2>$null }
}

AfterAll {
    if ($script:StubProc -and -not $script:StubProc.HasExited) { Stop-Process -Id $script:StubProc.Id -Force }
}

Describe 'idea-turn.ps1' {
    BeforeEach {
        $env:FACTORY_HOME = Join-Path $TestDrive "state-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Force $env:FACTORY_HOME | Out-Null
        $script:Target = Join-Path $TestDrive "target-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Force (Join-Path $script:Target 'docs') | Out-Null
        Set-Content (Join-Path $script:Target 'README.md') 'the readme of the target'
        Set-Content (Join-Path $script:Target 'docs/DESIGN.md') "# Design`nUse the Button component."
        Set-Content (Join-Path $TestDrive 'outside.txt') 'secret outside the fence'
        $script:EnvFile = Join-Path $TestDrive "env-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Set-Content $script:EnvFile "# test`nANTHROPIC_API_KEY=`"$script:Key`"`nANTHROPIC_BASE_URL=http://localhost:$script:Port"
        Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
    }

    It 'streams the reply, records it, and leaves a UI tool pending with the turn ended' {
        Set-Replies (Reply @((Text 'Hello there. A few questions first.'), (ToolUse 'tu1' 'ask_questions' $script:Ask)) 'tool_use')
        New-Session | Out-Null
        Run-Turn
        $s = Session
        $s.status | Should -Be 'idle'
        $s.pending.id | Should -Be 'tu1'
        $s.pending.name | Should -Be 'ask_questions'
        $s.pending.input.questions[0].options[1].label | Should -Be 'A team'
        $chat = Chat
        $chat.Count | Should -Be 2
        $chat[1].role | Should -Be 'assistant'
        $chat[1].content[1].input.questions[0].question | Should -Be 'Who uses it?'
        $st = @(Stream)
        (($st | Where-Object type -eq 'text').text -join '') | Should -Be 'Hello there. A few questions first.'
        $st[-1].type | Should -Be 'end'
        Test-Path (Join-Path $env:FACTORY_HOME 'sessions/S-001/turn.lock') | Should -BeFalse
    }

    It 'sends the key from the env file as x-api-key, never puts it in the environment or on disk, and uses the default model' {
        Set-Replies (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        Get-Content (Join-Path $script:ReplyDir 'requests/001.key') -Raw | Should -Be $script:Key
        Test-Path Env:ANTHROPIC_API_KEY | Should -BeFalse
        foreach ($f in Get-ChildItem (Join-Path $env:FACTORY_HOME 'sessions/S-001') -File) {
            Get-Content $f.FullName -Raw | Should -Not -Match ([regex]::Escape($script:Key))
        }
        $r = Request 1
        $r.model | Should -Be 'claude-opus-5-5'
        $r.stream | Should -BeTrue
        (Session).model | Should -Be 'claude-opus-5-5'
    }

    It 'FACTORY_IDEA_MODEL in the env file overrides the model' {
        Add-Content $script:EnvFile 'FACTORY_IDEA_MODEL=claude-sonnet-5-5'
        Set-Replies (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        (Request 1).model | Should -Be 'claude-sonnet-5-5'
    }

    It 'builds the system prompt from the preamble, the grill skill, the spec-writer and both templates, cached' {
        Set-Replies (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        $r = Request 1
        $r.system[0].cache_control.type | Should -Be 'ephemeral'
        $sys = $r.system[0].text
        $sys | Should -Match 'idea session, in the dashboard'
        $sys | Should -Match 'rounds over a frontier'
        $sys | Should -Match 'You are the factory''s spec-writer'
        $sys | Should -Match '# Brief: <one-line title>'
        $sys | Should -Match '# Spec:'
        $sys | Should -Not -Match '(?m)^name: grill'
        $names = $r.tools.name
        foreach ($t in 'read_file', 'list_dir', 'grep', 'submit_specs', 'revise_spec', 'ask_questions', 'propose_scaffold', 'submit_brief') { $names | Should -Contain $t }
    }

    It 'runs a read-only tool on the target and loops until the model stops' {
        Set-Replies (Reply @((ToolUse 'r1' 'read_file' @{ path = 'README.md' })) 'tool_use') (Reply @((Text 'Read it.')))
        New-Session | Out-Null
        Run-Turn
        (Requests) | Should -Be 2
        $last = (Request 2).messages[-1]
        $last.role | Should -Be 'user'
        $last.content[0].type | Should -Be 'tool_result'
        $last.content[0].tool_use_id | Should -Be 'r1'
        $last.content[0].content | Should -Match 'the readme of the target'
        @(Chat).Count | Should -Be 4
        (Session).pending | Should -BeNullOrEmpty
        (Stream | Where-Object type -eq 'tool').name | Should -Contain 'read_file'
    }

    It 'list_dir and grep work inside the target' {
        Set-Replies (Reply @((ToolUse 'l1' 'list_dir' @{}), (ToolUse 'g1' 'grep' @{ pattern = 'Button' })) 'tool_use') (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        $res = (Request 2).messages[-1].content
        $res[0].content | Should -Match 'docs/'
        $res[0].content | Should -Match 'README.md'
        $res[1].content | Should -Match 'DESIGN.md:2'
    }

    It 'refuses a read outside the target and the templates, whether by .. or an absolute path' {
        $outside = Join-Path $TestDrive 'outside.txt'
        Set-Replies (Reply @((ToolUse 'r1' 'read_file' @{ path = '..\outside.txt' }), (ToolUse 'r2' 'read_file' @{ path = $outside }),
                             (ToolUse 'r3' 'read_file' @{ path = (Join-Path $PSScriptRoot '..\templates\spec.md') })) 'tool_use') (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        $res = (Request 2).messages[-1].content
        $res[0].is_error | Should -BeTrue
        $res[0].content | Should -Not -Match 'secret'
        $res[1].is_error | Should -BeTrue
        $res[1].content | Should -Not -Match 'secret'
        $res[2].is_error | Should -Not -BeTrue
        $res[2].content | Should -Match '# Spec:'
    }

    It 'refuses submit_specs before the brief is confirmed, and writes the specs once there is an idea' {
        $spec = "# Spec: Add a todo`n`n## Target`n- Repo: $script:Target`n`n## Gate`n- build: ``exit 0``"
        Set-Replies (Reply @((ToolUse 's1' 'submit_specs' @{ specs = @(@{ k = 1; markdown = $spec }) })) 'tool_use') (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        (Request 2).messages[-1].content[0].is_error | Should -BeTrue
        @((Session).specs).Count | Should -Be 0

        Set-Replies (Reply @((ToolUse 's2' 'submit_specs' @{ specs = @(@{ k = 1; markdown = $spec }, @{ k = 2; markdown = ($spec -replace 'Add a todo', 'Check one off') }) })) 'tool_use') (Reply @((Text 'Two specs.')))
        New-Session -idea 'I-001' | Out-Null
        Run-Turn
        Get-Content (Join-Path $env:FACTORY_HOME 'ideas/I-001/spec-2.md') -Raw | Should -Match '# Spec: Check one off'
        $specs = (Session).specs
        $specs.Count | Should -Be 2
        $specs[0].title | Should -Be 'Add a todo'
        $specs[1].decision | Should -BeNullOrEmpty
    }

    It 'revise_spec overwrites an undecided spec' {
        $spec = "# Spec: Add a todo`n`n## Target`n- Repo: $script:Target`n`n## Gate`n- build: ``exit 0``"
        Set-Replies (Reply @((ToolUse 's1' 'submit_specs' @{ specs = @(@{ k = 1; markdown = $spec }) })) 'tool_use') (Reply @((ToolUse 'v1' 'revise_spec' @{ k = 1; markdown = ($spec -replace 'Add a todo', 'Add a todo item') })) 'tool_use') (Reply @((Text 'Revised.')))
        New-Session -idea 'I-001' | Out-Null
        Run-Turn
        Get-Content (Join-Path $env:FACTORY_HOME 'ideas/I-001/spec-1.md') -Raw | Should -Match '# Spec: Add a todo item'
        (Session).specs[0].title | Should -Be 'Add a todo item'
    }

    It 'keeps the results of server tools that ran beside a UI tool for the human''s answer' {
        Set-Replies (Reply @((ToolUse 'r1' 'read_file' @{ path = 'README.md' }), (ToolUse 'tu1' 'ask_questions' $script:Ask)) 'tool_use')
        New-Session | Out-Null
        Run-Turn
        $s = Session
        $s.pending.id | Should -Be 'tu1'
        $s.pendingResults[0].tool_use_id | Should -Be 'r1'
        $s.pendingResults[0].content | Should -Match 'the readme'
        (Requests) | Should -Be 1
    }

    It 'adds the turn''s cost at the model''s prices, cache reads and writes included' {
        Set-Replies (Reply @((Text 'ok')) 'end_turn' @{ input_tokens = 1000; cache_read_input_tokens = 2000; cache_creation_input_tokens = 400; output_tokens = 500 })
        New-Session | Out-Null
        Run-Turn
        [Math]::Round((Session).costUsd, 6) | Should -Be 0.0164
    }

    It 'an API error leaves the session in error, the chat unchanged and the lock released' {
        Set-Replies @{ status = 400; body = @{ type = 'error'; error = @{ type = 'invalid_request_error'; message = 'bad thing' } } }
        New-Session | Out-Null
        Run-Turn
        $s = Session
        $s.status | Should -Be 'error'
        $s.error | Should -Match 'bad thing'
        @(Chat).Count | Should -Be 1
        (Stream | Where-Object type -eq 'error').message | Should -Match 'bad thing'
        Test-Path (Join-Path $env:FACTORY_HOME 'sessions/S-001/turn.lock') | Should -BeFalse
    }

    It 'no key in the env file is an error, and no request is made' {
        Set-Content $script:EnvFile "ANTHROPIC_BASE_URL=http://localhost:$script:Port"
        Set-Replies (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        (Session).error | Should -Match 'ANTHROPIC_API_KEY'
        (Requests) | Should -Be 0
    }

    It 'refuses to start while a live turn holds the lock' {
        Set-Replies (Reply @((Text 'ok')))
        $dir = New-Session
        Set-Content (Join-Path $dir 'turn.lock') $PID
        Run-Turn
        $LASTEXITCODE | Should -Not -Be 0
        (Requests) | Should -Be 0
        Test-Path (Join-Path $dir 'turn.lock') | Should -BeTrue
    }

    It 'takes over a lock whose process is gone' {
        Set-Replies (Reply @((Text 'ok')))
        $dir = New-Session
        Set-Content (Join-Path $dir 'turn.lock') 999999
        Run-Turn
        (Requests) | Should -Be 1
        (Session).status | Should -Be 'idle'
    }

    It 'saves the session while a reader briefly holds session.json open, as serve.ps1 does when the dashboard polls' {
        Set-Replies (Reply @((Text 'ok')))
        $dir = New-Session
        $fs = [IO.File]::Open((Join-Path $dir 'session.json'), 'Open', 'Read', 'Read')
        $job = Start-ThreadJob -ArgumentList $fs { param($f) Start-Sleep -Milliseconds 700; $f.Dispose() }
        try { Run-Turn } finally { $job | Wait-Job | Remove-Job; $fs.Dispose() }
        (Session).status | Should -Be 'idle'
        @(Chat).Count | Should -Be 2
    }

    It 'a refusal ends the turn in error and runs none of its tools' {
        Set-Replies (Reply @((ToolUse 'r1' 'read_file' @{ path = 'README.md' })) 'refusal')
        New-Session | Out-Null
        Run-Turn
        (Session).status | Should -Be 'error'
        (Requests) | Should -Be 1
    }
}

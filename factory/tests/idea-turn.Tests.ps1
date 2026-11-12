#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

# One idea turn (roadmap spec 10): idea-turn.ps1 forwards to the .NET CLI's idea-turn under every FACTORY_IMPL, which
# runs claude -p with the idea tools as an MCP server. claude here is fixtures/claude-stub.ps1 (FACTORY_IDEA_CLAUDE_CMD):
# it starts the real MCP server and asks anthropic-stub.ps1 for each scripted model reply, so the tools run for real.
BeforeAll {
    . (Join-Path $PSScriptRoot 'fixtures/portable.ps1')   # -WindowStyle off Windows
    $script:Turn = Join-Path $PSScriptRoot '..\scripts\idea-turn.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Stub = Join-Path $PSScriptRoot 'fixtures\anthropic-stub.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    $script:Claude = Join-Path $PSScriptRoot 'fixtures\claude-stub.ps1' | Resolve-Path | Select-Object -ExpandProperty Path

    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $script:Port = $l.LocalEndpoint.Port; $l.Stop()
    $script:StubDir = Join-Path $TestDrive 'stub'
    New-Item -ItemType Directory -Force $script:StubDir | Out-Null
    $script:StubProc = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $script:Stub, '-Port', $script:Port, '-Dir', $script:StubDir -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(20)
    while ($true) {
        try { Invoke-WebRequest "http://localhost:$script:Port/" -TimeoutSec 1 | Out-Null; break }
        catch { if ((Get-Date) -gt $deadline) { throw 'stub did not start' }; Start-Sleep -Milliseconds 200 }
    }
    $env:FACTORY_IDEA_CLAUDE_CMD = $script:Claude
    $env:FACTORY_IDEA_STUB_URL = "http://localhost:$script:Port"

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
    # A tool result's text: claude passes MCP's content blocks.
    function Result-Text($block) { (@($block.content) | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.text } }) -join "`n" }
    # What the last claude was given: args, prompt, cwd and the provider variables it saw.
    function Last-Run { Read-J (Join-Path $env:FACTORY_IDEA_STUB_STATE 'last-run.json') }

    function Text([string] $t) { @{ type = 'text'; text = $t } }
    function ToolUse([string] $id, [string] $name, $in) { @{ type = 'tool_use'; id = $id; name = $name; input = $in } }
    function Reply([object[]] $content, [string] $stop = 'end_turn', $usage = @{}) { @{ content = $content; stop_reason = $stop; usage = $usage } }
    $script:Ask = @{ questions = @(@{ question = 'Who uses it?'; header = 'Users'; multiSelect = $false
                                        options = @(@{ label = 'Just me'; description = 'one person' }, @{ label = 'A team'; description = 'several' }) }) }

    # The read tools are fenced to the session's clone of its GitHub target (checkout), here $script:Target.
    function New-Session([string] $checkout = $script:Target, [string] $idea = $null, [switch] $NoTarget) {
        $target = if ($NoTarget) { $null } else { 'test/todo-app' }
        $dir = Join-Path $env:FACTORY_HOME 'sessions/S-001'
        New-Item -ItemType Directory -Force $dir | Out-Null
        Set-Content (Join-Path $dir 'session.json') (Json ([ordered]@{
            id = 'S-001'; target = $target; checkout = $checkout; text = 'a todo app'; newTarget = $false; status = 'idle'; error = $null
            model = $null; costUsd = 0; scaffold = $null; idea = $idea; bootstrap = $null; pending = $null; pendingResults = @(); specs = @()
            createdAt = '2026-10-17T00:00:00Z' }))
        Set-Content (Join-Path $dir 'chat.json') (Json @{ messages = @(@{ role = 'user'; content = @(@{ type = 'text'; text = "Target: $target`n`nIdea: a todo app" }) }) })
        $dir
    }
    function Session { Read-J (Join-Path $env:FACTORY_HOME 'sessions/S-001/session.json') }
    function Chat { (Read-J (Join-Path $env:FACTORY_HOME 'sessions/S-001/chat.json')).messages }
    function Stream { Get-Content (Join-Path $env:FACTORY_HOME 'sessions/S-001/stream.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } }
    function Run-Turn([string] $envFile = $script:EnvFile) { & $script:Turn -Session S-001 -StateDir $env:FACTORY_HOME -EnvFile $envFile 6>$null 2>$null }
    # What the dashboard does when the human answers the pending UI tool (serve's Add-Answer).
    function Answer([string] $text) {
        $dir = Join-Path $env:FACTORY_HOME 'sessions/S-001'
        $s = Session
        $chat = @(Chat)
        $chat += @{ role = 'user'; content = @($s.pendingResults) + @{ type = 'tool_result'; tool_use_id = $s.pending.id; content = $text } }
        $s.pending = $null; $s.pendingResults = @()
        Set-Content (Join-Path $dir 'chat.json') (Json @{ messages = $chat })
        Set-Content (Join-Path $dir 'session.json') (Json $s)
    }
}

AfterAll {
    if ($script:StubProc -and -not $script:StubProc.HasExited) { Stop-Process -Id $script:StubProc.Id -Force }
    Remove-Item Env:FACTORY_IDEA_CLAUDE_CMD, Env:FACTORY_IDEA_STUB_URL, Env:FACTORY_IDEA_STUB_STATE -ErrorAction SilentlyContinue
}

Describe 'idea-turn.ps1' {
    BeforeEach {
        $env:FACTORY_HOME = Join-Path $TestDrive "state-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Force $env:FACTORY_HOME | Out-Null
        $env:FACTORY_IDEA_STUB_STATE = Join-Path $TestDrive "claude-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        $script:Target = Join-Path $TestDrive "target-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Force (Join-Path $script:Target 'docs') | Out-Null
        Set-Content (Join-Path $script:Target 'README.md') 'the readme of the target'
        Set-Content (Join-Path $script:Target 'docs/DESIGN.md') "# Design`nUse the Button component."
        Set-Content (Join-Path $TestDrive 'outside.txt') 'secret outside the fence'
        $script:EnvFile = Join-Path $TestDrive "env-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Set-Content $script:EnvFile '# test'
        $env:ANTHROPIC_API_KEY = 'sk-ant-stray-in-the-environment'
    }
    AfterEach { Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue }

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
        $chat[1].content[1].name | Should -Be 'ask_questions'
        $chat[1].content[1].input.questions[0].question | Should -Be 'Who uses it?'
        $st = @(Stream)
        (($st | Where-Object type -eq 'text').text -join '') | Should -Be 'Hello there. A few questions first.'
        $st[-1].type | Should -Be 'end'
        Test-Path (Join-Path $env:FACTORY_HOME 'sessions/S-001/turn.lock') | Should -BeFalse
    }

    It 'runs claude headless with only the idea tools, the prompt on stdin, the default model and a new session' {
        Set-Replies (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        $run = Last-Run
        $a = $run.args -join ' '
        $a | Should -Match '^-p '
        $a | Should -Match '--output-format stream-json --verbose --include-partial-messages'
        $a | Should -Match '--model claude-opus-5-5 --effort high'
        $a | Should -Match '--strict-mcp-config --mcp-config \S+ --allowedTools mcp__factory'
        $a | Should -Match '--session-id [0-9a-f-]{36}'
        $a | Should -Not -Match '--resume'
        $run.prompt | Should -Match 'Idea: a todo app'
        $a | Should -Not -Match 'a todo app'
        $run.cwd | Should -Be (Resolve-Path $script:Target).Path
        (Request 1).tools.name | Should -Contain 'mcp__factory__read_file'
        $s = Session
        $s.model | Should -Be 'claude-opus-5-5'
        $s.claudeSession | Should -Match '^[0-9a-f-]{36}$'
    }

    It 'locally the subscription runs it: no API key or Foundry variable reaches claude' {
        Set-Replies (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        $e = (Last-Run).env
        $e.ANTHROPIC_API_KEY | Should -BeNullOrEmpty
        $e.CLAUDE_CODE_USE_FOUNDRY | Should -BeNullOrEmpty
        $e.ANTHROPIC_FOUNDRY_RESOURCE | Should -BeNullOrEmpty
    }

    It 'FACTORY_IDEA_MODEL in the env file overrides the model' {
        Add-Content $script:EnvFile 'FACTORY_IDEA_MODEL=claude-sonnet-5-5'
        Set-Replies (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        (Request 1).model | Should -Be 'claude-sonnet-5-5'
        (Session).model | Should -Be 'claude-sonnet-5-5'
    }

    It 'builds the system prompt from the preamble, the grill skill, the spec-writer and both templates' {
        Set-Replies (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        $r = Request 1
        $sys = $r.system
        $sys | Should -Match 'idea session, in the dashboard'
        $sys | Should -Match 'rounds over a frontier'
        $sys | Should -Match 'You are the factory''s spec-writer'
        $sys | Should -Match '# Brief: <one-line title>'
        $sys | Should -Match '# Spec:'
        $sys | Should -Not -Match '(?m)^name: grill'
        $names = $r.tools.name
        foreach ($t in 'read_file', 'list_dir', 'grep', 'submit_specs', 'revise_spec', 'ask_questions', 'propose_scaffold', 'submit_brief') { $names | Should -Contain "mcp__factory__$t" }
    }

    It 'runs a read-only tool on the target and loops until the model stops' {
        Set-Replies (Reply @((ToolUse 'r1' 'read_file' @{ path = 'README.md' })) 'tool_use') (Reply @((Text 'Read it.')))
        New-Session | Out-Null
        Run-Turn
        (Requests) | Should -Be 2
        $last = (Request 2).messages[-1]
        $last.role | Should -Be 'user'
        $last.content[0].tool_use_id | Should -Be 'r1'
        Result-Text $last.content[0] | Should -Match 'the readme of the target'
        $chat = @(Chat)
        $chat.Count | Should -Be 4
        $chat[1].content[0].name | Should -Be 'read_file'
        $chat[2].content[0].content | Should -Match 'the readme of the target'
        (Session).pending | Should -BeNullOrEmpty
        (Stream | Where-Object type -eq 'tool').name | Should -Contain 'read_file'
    }

    It 'list_dir and grep work inside the target' {
        Set-Replies (Reply @((ToolUse 'l1' 'list_dir' @{}), (ToolUse 'g1' 'grep' @{ pattern = 'button' })) 'tool_use') (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        $res = (Request 2).messages[-1].content
        Result-Text $res[0] | Should -Match 'docs/'
        Result-Text $res[0] | Should -Match 'README.md'
        Result-Text $res[1] | Should -Match 'DESIGN.md:2'
    }

    It 'refuses a read outside the target and the templates, whether by .. or an absolute path' {
        $outside = Join-Path $TestDrive 'outside.txt'
        Set-Replies (Reply @((ToolUse 'r1' 'read_file' @{ path = '..\outside.txt' }), (ToolUse 'r2' 'read_file' @{ path = $outside }),
                             (ToolUse 'r3' 'read_file' @{ path = (Join-Path $PSScriptRoot '..\templates\spec.md') })) 'tool_use') (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        $res = @(Chat)[2].content
        $res[0].is_error | Should -BeTrue
        $res[0].content | Should -Not -Match 'secret'
        $res[1].is_error | Should -BeTrue
        $res[1].content | Should -Not -Match 'secret'
        $res[2].is_error | Should -Not -BeTrue
        $res[2].content | Should -Match '# Spec:'
    }

    It 'refuses propose_scaffold without a repo while the session has no target' {
        $sc = @{ name = 'todo'; resource = 'Todo'; db = 'sqlite'; ui = 'react'; events = $false }
        Set-Replies (Reply @((ToolUse 'p1' 'propose_scaffold' $sc)) 'tool_use') (Reply @((ToolUse 'p2' 'propose_scaffold' ($sc + @{ repo = 'todo-app' }))) 'tool_use')
        New-Session -NoTarget | Out-Null
        Run-Turn
        (Request 2).messages[-1].content[0].is_error | Should -BeTrue
        Result-Text (Request 2).messages[-1].content[0] | Should -Match 'repo'
        (Session).pending.input.repo | Should -Be 'todo-app'
        (Session).pending.id | Should -Be 'p2'
    }

    It 'a UI tool ends the turn: any tool after it is refused' {
        Set-Replies (Reply @((ToolUse 'tu1' 'ask_questions' $script:Ask), (ToolUse 'r1' 'read_file' @{ path = 'README.md' })) 'tool_use') (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        $s = Session
        $s.pending.id | Should -Be 'tu1'
        $s.pendingResults[0].tool_use_id | Should -Be 'r1'
        $s.pendingResults[0].is_error | Should -BeTrue
        $s.pendingResults[0].content | Should -Match 'your turn ended with ask_questions'
    }

    It 'refuses submit_specs before the brief is confirmed, and writes the specs once there is an idea' {
        $spec = "# Spec: Add a todo`n`n## Target`n- Repo: $script:Target`n`n## Gate`n- build: ``exit 0``"
        Set-Replies (Reply @((ToolUse 's1' 'submit_specs' @{ specs = @(@{ k = 1; markdown = $spec }) })) 'tool_use') (Reply @((Text 'ok')))
        New-Session | Out-Null
        Run-Turn
        (Request 2).messages[-1].content[0].is_error | Should -BeTrue
        @((Session).specs).Count | Should -Be 0

        Set-Replies (Reply @((ToolUse 's2' 'submit_specs' @{ specs = @(@{ k = 1; markdown = $spec }, @{ k = 2; markdown = ($spec -replace 'Add a todo', 'Check one off') }) })) 'tool_use') (Reply @((Text 'Two specs.')))
        $env:FACTORY_IDEA_STUB_STATE = Join-Path $TestDrive "claude-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
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
        @(Chat).Count | Should -Be 2
        (Requests) | Should -Be 1
    }

    It 'the next turn resumes the claude session with the human''s answer as its prompt' {
        Set-Replies (Reply @((ToolUse 'tu1' 'ask_questions' $script:Ask)) 'tool_use') (Reply @((Text 'Thanks, just you then.')))
        New-Session | Out-Null
        Run-Turn
        $first = (Session).claudeSession
        Answer 'Just me'
        Run-Turn
        $run = Last-Run
        ($run.args -join ' ') | Should -Match "--resume $first"
        $run.prompt | Should -Be 'The human answered ask_questions: Just me'
        (Session).claudeSession | Should -Be $first
        (Request 2).messages[-1].content[0].text | Should -Be 'The human answered ask_questions: Just me'
        $chat = @(Chat)
        $chat[-1].content[0].text | Should -Be 'Thanks, just you then.'
        $chat[-2].content[0].tool_use_id | Should -Be 'tu1'
    }

    It 'adds the turn''s cost from claude''s result' {
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

    It 'a claude that exits without a result leaves the session in error' {
        $broken = Join-Path $TestDrive 'broken-claude.ps1'
        Set-Content $broken '[Console]::Error.WriteLine("claude: not logged in"); exit 1'
        $env:FACTORY_IDEA_CLAUDE_CMD = $broken
        try {
            New-Session | Out-Null
            Run-Turn
        } finally { $env:FACTORY_IDEA_CLAUDE_CMD = $script:Claude }
        $s = Session
        $s.status | Should -Be 'error'
        $s.error | Should -Match 'no result: claude: not logged in'
    }

    Context 'cloud mode (Foundry)' {
        BeforeEach { Add-Content $script:EnvFile "FACTORY_MODE=cloud`nFACTORY_FOUNDRY_RESOURCE=fdy-test" }

        It 'hands claude the Foundry environment and no key, with the default deployments' {
            Set-Replies (Reply @((Text 'ok')))
            New-Session | Out-Null
            Run-Turn
            (Session).status | Should -Be 'idle'
            $e = (Last-Run).env
            $e.CLAUDE_CODE_USE_FOUNDRY | Should -Be '1'
            $e.ANTHROPIC_FOUNDRY_RESOURCE | Should -Be 'fdy-test'
            $e.ANTHROPIC_DEFAULT_OPUS_MODEL | Should -Be 'claude-opus-5-5'
            $e.ANTHROPIC_API_KEY | Should -BeNullOrEmpty
            (Request 1).model | Should -Be 'claude-opus-5-5'
        }

        It 'is an error with no FACTORY_FOUNDRY_RESOURCE, and claude is not run' {
            Set-Content $script:EnvFile 'FACTORY_MODE=cloud'
            Set-Replies (Reply @((Text 'ok')))
            New-Session | Out-Null
            Run-Turn
            (Session).error | Should -Match 'FACTORY_FOUNDRY_RESOURCE'
            (Requests) | Should -Be 0
            Test-Path (Join-Path $env:FACTORY_IDEA_STUB_STATE 'last-run.json') | Should -BeFalse
        }
    }

    It 'refuses to start while a live turn holds the lock' {
        Set-Replies (Reply @((Text 'ok')))
        $dir = New-Session
        Set-Content (Join-Path $dir 'turn.lock') $PID
        Run-Turn
        $LASTEXITCODE | Should -Be 3
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

    It 'is exit 2 for a session that does not exist' {
        & $script:Turn -Session S-404 -StateDir $env:FACTORY_HOME -EnvFile $script:EnvFile 6>$null 2>$null
        $LASTEXITCODE | Should -Be 2
    }

    It 'saves the session while a reader briefly holds session.json open, as the dashboard does when it polls' {
        Set-Replies (Reply @((Text 'ok')))
        $dir = New-Session
        $fs = [IO.File]::Open((Join-Path $dir 'session.json'), 'Open', 'Read', 'Read')
        # Release once the writer is retrying (its temp file exists), well inside its ~2 s of retries.
        # Compiled, on a plain .NET thread: a thread job first builds a runspace, which on a loaded CI
        # runner took longer than the writer's retries (PR #7), as did a fixed 700 ms sleep before it.
        if (-not ('ReleaseWhenRetried' -as [type])) {
            Add-Type -TypeDefinition @'
using System; using System.IO; using System.Threading; using System.Threading.Tasks;
public static class ReleaseWhenRetried {
    public static Task Start(FileStream held, string dir) {
        return Task.Run(() => {
            var until = DateTime.UtcNow.AddSeconds(60);
            while (Directory.GetFiles(dir, "session.json.*.tmp").Length == 0 && DateTime.UtcNow < until) Thread.Sleep(20);
            Thread.Sleep(300);
            held.Dispose();
        });
    }
}
'@
        }
        $release = [ReleaseWhenRetried]::Start($fs, $dir)
        try { Run-Turn } finally { $release.Wait(65000) | Out-Null; $fs.Dispose() }
        (Session).status | Should -Be 'idle'
        @(Chat).Count | Should -Be 2
    }

    It 'a refusal ends the turn in error and runs none of its tools' {
        Set-Replies (Reply @((ToolUse 'r1' 'read_file' @{ path = 'README.md' })) 'refusal')
        New-Session | Out-Null
        Run-Turn
        (Session).status | Should -Be 'error'
        (Requests) | Should -Be 1
        (Stream | Where-Object type -eq 'tool') | Should -BeNullOrEmpty
    }
}

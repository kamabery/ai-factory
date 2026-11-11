#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

# factory.ps1 with FACTORY_STATE=azure against blob-stub.ps1: two FACTORY_HOMEs on one container
# stand in for two machines. See "State backends" in the contract.

BeforeAll {
    . (Join-Path $PSScriptRoot 'fixtures/portable.ps1')   # -WindowStyle off Windows
    $script:Factory = Join-Path $PSScriptRoot '..\scripts\factory.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    . (Join-Path $PSScriptRoot 'fixtures\github.ps1')
    . (Join-Path $PSScriptRoot 'fixtures\blob.ps1')
    Start-GitHubStub
    Start-BlobStub

    # A child process, as runner.ps1 calls it, so a refusal's stderr (Fail writes the console's) lands in $script:Err.
    function F {
        $script:Err = Join-Path $TestDrive "err-$([guid]::NewGuid().ToString('N')).txt"
        & pwsh -NoProfile -File $script:Factory @args 2>$script:Err
    }
    function Err { if (Test-Path $script:Err) { Get-Content $script:Err -Raw } else { '' } }
    function New-Home { Join-Path $TestDrive "home-$([guid]::NewGuid().ToString('N').Substring(0,8))" }
    function Use-Home([string] $h) { $env:FACTORY_HOME = $h }
    function Board { Get-Content (Join-Path $env:FACTORY_HOME 'board.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
    function New-Spec([string] $title) {
        $p = Join-Path $TestDrive "spec-$([guid]::NewGuid().ToString('N')).md"
        Set-Content $p "# Spec: $title`n`n## Intent`nDo $title.`n`n## Target`n- Repo: $script:Repo`n`n## Gate`n- build: ``exit 0```n"
        $p
    }
}

AfterAll { Stop-BlobStub; Stop-GitHubStub }

# Ported (roadmap spec 03): run-all.ps1 -Tag Ported runs this block with FACTORY_IMPL=ps and dotnet, so the
# .NET status shares one container, lease and .sync.json with factory.ps1's other commands.
Describe 'factory.ps1 on azure' -Tag 'Ported' {

BeforeEach {
    $script:Repo = "test/repo-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Remote $script:Repo
    Use-AzureState
    $script:A = New-Home; $script:B = New-Home
    Use-Home $script:A
    $env:FACTORY_WORK = Join-Path $TestDrive "work-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $env:FACTORY_ENV_FILE = $script:EnvFile
}

AfterEach {
    Clear-AzureState
    Remove-Item Env:FACTORY_HOME, Env:FACTORY_WORK, Env:FACTORY_ENV_FILE, Env:FACTORY_STATE_LOCK_SECONDS, Env:FACTORY_AZ_CMD,
        Env:FACTORY_TOOL_LOG, Env:FACTORY_IP_URL, Env:FACTORY_STORAGE_ACCOUNT -ErrorAction SilentlyContinue
}

It 'two homes work one board: what one writes, the other reads' {
    F new-idea 'shared idea' -Target $script:Repo | Should -Be 'I-001'
    Use-Home $script:B
    ((F status -Json) | ConvertFrom-Json).ideas[0].id | Should -Be 'I-001'
    Get-Content (Join-Path $script:B 'ideas/I-001/idea.md') -Raw | Should -Match 'shared idea'
    F add-spec 'I-001' (New-Spec 'From B') | Should -Be 'J-001'

    Use-Home $script:A
    (F status) -join "`n" | Should -Match 'J-001 +backlog'
    Test-Path (Join-Path $script:A 'jobs/J-001/spec.md') | Should -BeTrue
    $month = "events/$((Get-Date).ToUniversalTime().ToString('yyyy-MM')).jsonl"
    (Blob-Text $month) -split "`n" | Where-Object { $_ -match '"to":"backlog"' } | Should -Not -BeNullOrEmpty
}

It 'refuses a command while another machine holds the board lease, and leaves the lease free after any command' {
    F new-idea 'x' -Target $script:Repo | Out-Null
    (Blob-State).Blobs['locks/board'].leased | Should -BeFalse
    . (Join-Path $PSScriptRoot '..\scripts\state.ps1')
    Initialize-State -StateHome $script:B -EnvFile $null
    $held = Enter-StateLease 'board' 5
    try {
        $env:FACTORY_STATE_LOCK_SECONDS = '1'
        F status | Out-Null
        $LASTEXITCODE | Should -Be 1
        Err | Should -Match 'state lock refused'
    } finally { Exit-StateLease 'board' $held }
    F status | Out-Null
    $LASTEXITCODE | Should -Be 0
}

It "copies a subagent's transcript into the job at its agent-stop" {
    F new-idea 'x' -Target $script:Repo | Out-Null
    F add-spec 'I-001' (New-Spec 'T') | Out-Null
    $t = Join-Path $TestDrive "transcript-$([guid]::NewGuid().ToString('N')).jsonl"
    Set-Content $t '{"type":"user"}'
    F event (@{ job = 'J-001'; kind = 'agent-start'; agent = 'code-reviewer'; agentId = 'abc123'; transcript = $t } | ConvertTo-Json -Compress) | Out-Null
    F event (@{ job = 'J-001'; kind = 'agent-stop'; agent = 'code-reviewer' } | ConvertTo-Json -Compress) | Out-Null
    Blob-Text 'jobs/J-001/transcripts/agent-abc123.jsonl' | Should -Match '"user"'
}

It 'a local home must be migrated before azure takes it, then the container is its record' {
    $env:FACTORY_STATE = 'local'
    F new-idea 'from before' -Target $script:Repo | Out-Null
    $env:FACTORY_STATE = 'azure'
    F status | Out-Null
    $LASTEXITCODE | Should -Be 1
    Err | Should -Match 'state-migrate'
    F state-migrate | Should -Match '^migrated: \d+ blobs'
    Use-Home $script:B
    ((F status -Json) | ConvertFrom-Json).ideas[0].text | Should -Be 'from before'
    F state-pull | Should -Match '^pulled: 0'
    @(Get-Content (Join-Path $script:B 'events.jsonl')).Count | Should -BeGreaterThan 0
}

It "rolls a discarded job's run logs off when a push crosses the cap, and notes it" {
    F new-idea 'x' -Target $script:Repo | Out-Null
    F add-spec 'I-001' (New-Spec 'Cap') | Out-Null
    F discard 'J-001' | Out-Null
    Set-Content (Join-Path $script:A 'jobs/J-001/run-1.jsonl') ('x' * 20000)
    $env:FACTORY_STATE_CAP = '20000'
    F status | Out-Null
    (Blob-State).Blobs.ContainsKey('jobs/J-001/run-1.jsonl') | Should -BeFalse
    Test-Path (Join-Path $script:A 'jobs/J-001/run-1.jsonl') | Should -BeTrue
    Get-Content (Join-Path $script:A 'events.jsonl') | Where-Object { $_ -match '"detail":"pruned \d+ blobs' } | Should -Not -BeNullOrEmpty
    F prune | Should -Match '^\d+ of 20000 bytes$'
}

It "holds a building job's run logs back until it leaves the Building stages" {
    F new-idea 'x' -Target $script:Repo | Out-Null
    F add-spec 'I-001' (New-Spec 'Live') | Out-Null
    F approve 'J-001' | Out-Null
    F set-stage 'J-001' 'build' | Out-Null
    (Board).jobs[0].stage | Should -Be 'build'
    Set-Content (Join-Path $script:A 'jobs/J-001/run-1.jsonl') '{"type":"system"}'
    New-Item -ItemType Directory -Force (Join-Path $script:A 'jobs/J-001/round-1') | Out-Null
    Set-Content (Join-Path $script:A 'jobs/J-001/round-1/build.md') 'built'
    F status | Out-Null
    (Blob-State).Blobs.ContainsKey('jobs/J-001/run-1.jsonl') | Should -BeFalse
    (Blob-State).Blobs.ContainsKey('jobs/J-001/round-1/build.md') | Should -BeTrue
}

It 'a runner exits at once while another machine holds locks/runner, leaving the queue alone' {
    F new-idea 'x' -Target $script:Repo | Out-Null
    F add-spec 'I-001' (New-Spec 'Queued') | Out-Null
    F approve 'J-001' | Out-Null
    . (Join-Path $PSScriptRoot '..\scripts\state.ps1')
    Initialize-State -StateHome $script:B -EnvFile $null
    $held = Enter-StateLease 'runner' 5
    try {
        $env:FACTORY_CLAUDE_CMD = 'exit 0'
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\scripts\runner.ps1') 2>$null
        $LASTEXITCODE | Should -Be 0
    } finally { Exit-StateLease 'runner' $held; Remove-Item Env:FACTORY_CLAUDE_CMD -ErrorAction SilentlyContinue }
    ((F status -Json) | ConvertFrom-Json).jobs[0].stage | Should -Be 'approved'
    Test-Path (Join-Path $script:A 'runner.lock') | Should -BeFalse
    Test-Path (Join-Path $script:A 'runner.log') | Should -BeFalse
}

It 'an idea turn refuses while another machine holds its turn lease' {
    $dir = Join-Path $script:A 'sessions/S-001'
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content (Join-Path $dir 'session.json') '{"id":"S-001","status":"idle"}'
    . (Join-Path $PSScriptRoot '..\scripts\state.ps1')
    Initialize-State -StateHome $script:B -EnvFile $null
    $held = Enter-StateLease 'turn-S-001' 5
    try {
        $err = Join-Path $TestDrive "turn-$([guid]::NewGuid().ToString('N')).txt"
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\scripts\idea-turn.ps1') -Session S-001 -StateDir $script:A -EnvFile $script:EnvFile 2>$err
        $LASTEXITCODE | Should -Be 3
        Get-Content $err -Raw | Should -Match 'another machine'
    } finally { Exit-StateLease 'turn-S-001' $held }
    Test-Path (Join-Path $dir 'turn.lock') | Should -BeFalse
}

It "the dashboard shows another machine's board, events and transcripts" {
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
    $env:FACTORY_STATE_POLL_SECONDS = '0'
    New-Item -ItemType Directory -Force $script:A | Out-Null
    $serve = Start-Process pwsh -ArgumentList '-NoProfile', '-File', (Join-Path $PSScriptRoot '..\scripts\serve.ps1'), '-Port', $port,
        '-StateDir', $script:A, '-EnvFile', $script:EnvFile -PassThru -WindowStyle Hidden
    try {
        $deadline = (Get-Date).AddSeconds(30)
        while ($true) {
            try { Invoke-WebRequest "http://localhost:$port/" -TimeoutSec 1 | Out-Null; break }
            catch { if ((Get-Date) -gt $deadline) { throw 'serve did not start' }; Start-Sleep -Milliseconds 200 }
        }
        Use-Home $script:B
        F new-idea 'from b' -Target $script:Repo | Out-Null
        F add-spec 'I-001' (New-Spec 'B job') | Out-Null
        $t = Join-Path $TestDrive "transcript-$([guid]::NewGuid().ToString('N')).jsonl"
        Set-Content $t '{"type":"assistant"}'
        F event (@{ job = 'J-001'; kind = 'agent-start'; agent = 'code-reviewer'; agentId = 'b77'; transcript = $t } | ConvertTo-Json -Compress) | Out-Null
        F event (@{ job = 'J-001'; kind = 'agent-stop'; agent = 'code-reviewer' } | ConvertTo-Json -Compress) | Out-Null
        Remove-Item $t   # it lives only on machine B

        (Invoke-RestMethod "http://localhost:$port/state/board.json").ideas[0].text | Should -Be 'from b'
        (Invoke-WebRequest "http://localhost:$port/state/events.jsonl").Content | Should -Match '"agentId":"b77"'
        (Invoke-WebRequest "http://localhost:$port/api/agents/b77/transcript").Content | Should -Match '"assistant"'
        (Invoke-WebRequest "http://localhost:$port/api/jobs/J-001/files").Content | ConvertFrom-Json | Should -Contain 'spec.md'
    } finally {
        Stop-Process -Id $serve.Id -Force -ErrorAction SilentlyContinue
        Remove-Item Env:FACTORY_STATE_POLL_SECONDS -ErrorAction SilentlyContinue
    }
}

It 'allow-ip opens the firewall to this IP and closes the last one' {
    $env:FACTORY_AZ_CMD = Join-Path $PSScriptRoot 'fixtures\tool-stub.ps1'
    $env:FACTORY_TOOL_LOG = Join-Path $TestDrive "tools-$([guid]::NewGuid().ToString('N').Substring(0,8)).jsonl"
    $env:FACTORY_IP_URL = "http://localhost:$script:BlobPort/_stub/ip"
    $env:FACTORY_STORAGE_ACCOUNT = 'stfactorytest'
    Invoke-RestMethod -Method Put "http://localhost:$script:BlobPort/_stub/ip" -Body '203.0.113.7' | Out-Null
    F allow-ip | Should -Be 'allowed 203.0.113.7 on stfactorytest'
    Invoke-RestMethod -Method Put "http://localhost:$script:BlobPort/_stub/ip" -Body '198.51.100.9' | Out-Null
    F allow-ip | Should -Be 'allowed 198.51.100.9 on stfactorytest; removed 203.0.113.7'
    $calls = Get-Content $env:FACTORY_TOOL_LOG | ForEach-Object { ($_ | ConvertFrom-Json).args -join ' ' }
    $calls[0] | Should -Match '^storage account network-rule add --account-name stfactorytest --resource-group rg-factory-state --ip-address 203\.0\.113\.7'
    $calls[2] | Should -Match 'network-rule remove .*--ip-address 203\.0\.113\.7'
}

}

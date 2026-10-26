#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

# state.ps1, the azure state backend, against blob-stub.ps1. See "State backends" in the contract.

BeforeAll {
    Set-StrictMode -Version 3   # as factory.ps1 and serve.ps1 run state.ps1
    . (Join-Path $PSScriptRoot 'fixtures\blob.ps1')
    Start-BlobStub -LeaseScale 0.05   # a 60 s lease lasts 3 s
    $script:StateLib = Join-Path $PSScriptRoot '..\scripts\state.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    . $script:StateLib

    function New-Home { $h = Join-Path $TestDrive "home-$([guid]::NewGuid().ToString('N').Substring(0,8))"; New-Item -ItemType Directory -Force $h | Out-Null; $h }
    function Put([string] $root, [string] $rel, [string] $text) {
        $p = Join-Path $root $rel
        New-Item -ItemType Directory -Force (Split-Path $p -Parent) | Out-Null
        Set-Content -LiteralPath $p $text -NoNewline
    }
    function Bytes([string] $s) { [Text.Encoding]::UTF8.GetBytes($s) }
    function Text($b) { [Text.Encoding]::UTF8.GetString($b) }
}

AfterAll { Stop-BlobStub }

Describe 'state.ps1' {

BeforeEach {
    Use-AzureState
    $script:EnvFile = Join-Path $TestDrive 'none.env'
    $script:A = New-Home
    Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
}

AfterEach { Clear-AzureState; Remove-Item Env:FACTORY_AZ_CMD, Env:FACTORY_STATE_RENEW_SECONDS -ErrorAction SilentlyContinue }

Describe 'mode' {
    It 'is local unless FACTORY_STATE is azure, and then sync is a no-op' {
        $env:FACTORY_STATE = 'local'
        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        State-IsAzure | Should -BeFalse
        Put $script:A 'ideas/I-001/idea.md' 'x'
        Push-State | Should -Be 0
        (Blob-State).Blobs.Count | Should -Be 0
    }

    It 'refuses azure without FACTORY_BLOB_URL' {
        Remove-Item Env:FACTORY_BLOB_URL
        { Initialize-State -StateHome $script:A -EnvFile $script:EnvFile } | Should -Throw '*FACTORY_BLOB_URL*'
    }

    It 'reads FACTORY_STATE and FACTORY_BLOB_URL from .env when the environment has neither' {
        $url = $env:FACTORY_BLOB_URL
        Remove-Item Env:FACTORY_STATE, Env:FACTORY_BLOB_URL
        Set-Content $script:EnvFile @('FACTORY_STATE=azure', "FACTORY_BLOB_URL=$url")
        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        State-IsAzure | Should -BeTrue
        Remove-Item $script:EnvFile
    }
}

Describe 'blobs' {
    It 'writes, reads, answers not-modified for its ETag, and refuses a stale If-Match' {
        $etag = Write-StateBlob 'a/b.txt' (Bytes 'one')
        $r = Read-StateBlob 'a/b.txt'
        Text $r.Bytes | Should -Be 'one'
        $r.ETag | Should -Be $etag
        (Read-StateBlob 'a/b.txt' -ETag $etag).NotModified | Should -BeTrue
        $null = Write-StateBlob 'a/b.txt' (Bytes 'two') -IfMatch $etag
        { Write-StateBlob 'a/b.txt' (Bytes 'three') -IfMatch $etag } | Should -Throw '*state conflict*'
        Text (Read-StateBlob 'a/b.txt').Bytes | Should -Be 'two'
        Read-StateBlob 'missing' | Should -BeNullOrEmpty
    }

    It 'sends the bearer token, and a refused token is an error naming the code' {
        $env:FACTORY_STATE_TOKEN = 'wrong'
        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        { Read-StateBlob 'x' } | Should -Throw '*403*AuthorizationPermissionMismatch*'
    }

    It 'lists across pages, with versions on request' {
        foreach ($i in 1..3) { $null = Write-StateBlob "p/$i" (Bytes "$i") }
        $null = Write-StateBlob 'p/1' (Bytes 'again')
        @(Get-StateList -Prefix 'p/').Count | Should -Be 3
        $all = @(Get-StateList -Prefix 'p/' -Versions)
        $all.Count | Should -Be 4
        @($all | Where-Object { -not $_.IsCurrent }).Name | Should -Be 'p/1'
        ($all | Where-Object { $_.Name -eq 'p/3' }).Length | Should -Be 1
    }
}

Describe 'leases' {
    It 'one holder at a time; a released lease can be taken again' {
        $id = Enter-StateLease 'board' -TimeoutSeconds 1
        $id | Should -Not -BeNullOrEmpty
        Enter-StateLease 'board' -TimeoutSeconds 1 | Should -BeNullOrEmpty
        Exit-StateLease 'board' $id
        Enter-StateLease 'board' -TimeoutSeconds 1 | Should -Not -BeNullOrEmpty
    }

    It 'a lapsed lease is taken over, and its old holder can no longer renew it' {
        $old = Enter-StateLease 'runner' -TimeoutSeconds 1
        Start-Sleep -Seconds 4
        $new = Enter-StateLease 'runner' -TimeoutSeconds 1
        $new | Should -Not -BeNullOrEmpty
        Update-StateLease 'runner' $old | Should -BeFalse
        Update-StateLease 'runner' $new | Should -BeTrue
    }

    It 'a keeper renews the lease while its holder works' {
        $env:FACTORY_STATE_RENEW_SECONDS = '1'
        $id = Enter-StateLease 'canary' -TimeoutSeconds 1
        $k = Start-StateLeaseKeeper 'canary' $id
        try {
            Start-Sleep -Seconds 5
            Enter-StateLease 'canary' -TimeoutSeconds 1 | Should -BeNullOrEmpty
        } finally { Stop-StateLeaseKeeper $k }
        Exit-StateLease 'canary' $id
        Enter-StateLease 'canary' -TimeoutSeconds 1 | Should -Not -BeNullOrEmpty
    }
}

Describe 'push and pull' {
    It 'pushes changed files only, skips the excluded ones, and a second home pulls them' {
        Put $script:A 'ideas/I-001/idea.md' 'idea'
        Put $script:A 'jobs/J-001/spec.md' 'spec'
        Put $script:A 'jobs/J-001/round-1/build.md' 'build'
        Put $script:A 'board.json.123.tmp' 'tmp'
        Put $script:A 'runner.lock' '42'
        Put $script:A 'headless-settings.json' '{}'
        Push-State | Should -Be 3
        Push-State | Should -Be 0
        (Blob-State).Blobs.Keys | Sort-Object | Should -Be @('ideas/I-001/idea.md', 'jobs/J-001/round-1/build.md', 'jobs/J-001/spec.md')

        $b = New-Home
        Initialize-State -StateHome $b -EnvFile $script:EnvFile
        Pull-State | Should -Be 3
        Get-Content (Join-Path $b 'jobs/J-001/round-1/build.md') -Raw | Should -Be 'build'
        Pull-State | Should -Be 0
        Pull-State -Prefix 'jobs/J-001/' | Should -Be 0
    }

    It 'a pull never overwrites a file changed locally since its last sync, and never deletes one' {
        Put $script:A 'jobs/J-001/spec.md' 'v1'
        $null = Push-State
        $b = New-Home
        Initialize-State -StateHome $b -EnvFile $script:EnvFile
        $null = Pull-State
        Put $b 'jobs/J-001/spec.md' 'edited on b'
        Put $b 'jobs/J-001/only-b.md' 'mine'

        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        Put $script:A 'jobs/J-001/spec.md' 'v2 from a'
        $null = Push-State

        Initialize-State -StateHome $b -EnvFile $script:EnvFile
        Pull-State | Should -Be 0
        Get-Content (Join-Path $b 'jobs/J-001/spec.md') -Raw | Should -Be 'edited on b'
        Test-Path (Join-Path $b 'jobs/J-001/only-b.md') | Should -BeTrue
    }

    It 'a file deleted locally is deleted from the container on the next push' {
        Put $script:A 'jobs/J-001/spec.md' 'spec'
        $null = Push-State
        Remove-Item -Recurse (Join-Path $script:A 'jobs/J-001')
        $null = Push-State
        (Blob-State).Blobs.ContainsKey('jobs/J-001/spec.md') | Should -BeFalse
    }

    It 'the -Skip filter holds a file back until it no longer matches' {
        Put $script:A 'jobs/J-001/run-1.jsonl' '{}'
        Push-State -Skip { param($rel) $rel -like 'jobs/J-001/run-*' } | Should -Be 0
        Push-State | Should -Be 1
    }
}

Describe 'board and events' {
    It 'a second home sees the board change, and its stale write is refused' {
        Save-StateBoard '{"version":2,"jobs":[]}'
        $b = New-Home
        Initialize-State -StateHome $b -EnvFile $script:EnvFile
        Sync-Board | Should -Be 'changed'
        Sync-Board | Should -Be 'same'
        Get-Content (Join-Path $b 'board.json') -Raw | Should -Be '{"version":2,"jobs":[]}'

        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        Save-StateBoard '{"version":2,"jobs":["from a"]}'

        Initialize-State -StateHome $b -EnvFile $script:EnvFile
        { Save-StateBoard '{"version":2,"jobs":["from b"]}' } | Should -Throw '*state conflict*'
        Sync-Board | Should -Be 'changed'
        Save-StateBoard '{"version":2,"jobs":["from b"]}'
        Blob-Text 'board.json' | Should -Be '{"version":2,"jobs":["from b"]}'
    }

    It 'an empty container and no local board is a new board; a local board the container lacks needs state-migrate' {
        Sync-Board | Should -Be 'none'
        Put $script:A 'board.json' '{"version":2}'
        { Sync-Board } | Should -Throw '*state-migrate*'
    }

    It 'events append locally and to the month blob; another home rebuilds events.jsonl from the months' {
        Add-StateEvent '{"ts":"2026-10-01T00:00:00.000Z","kind":"note","detail":"one"}'
        Add-StateEvent '{"ts":"2026-10-01T00:00:01.000Z","kind":"note","detail":"two"}'
        @(Get-Content (Join-Path $script:A 'events.jsonl')).Count | Should -Be 2
        $month = "events/$((Get-Date).ToUniversalTime().ToString('yyyy-MM')).jsonl"
        (Blob-State).Blobs[$month].type | Should -Be 'AppendBlob'

        $b = New-Home
        Initialize-State -StateHome $b -EnvFile $script:EnvFile
        Sync-Events | Should -BeTrue
        Sync-Events | Should -BeFalse
        (Get-Content (Join-Path $b 'events.jsonl'))[1] | Should -Match '"two"'
    }

    It 'state-migrate uploads an existing home once, and refuses when the container already has a board' {
        Put $script:A 'board.json' '{"version":2,"jobs":[]}'
        Put $script:A 'events.jsonl' "{`"ts`":`"2026-09-30T23:00:00.000Z`",`"kind`":`"note`"}`n{`"ts`":`"2026-10-02T00:00:00.000Z`",`"kind`":`"note`"}`n"
        Put $script:A 'evals/J-001.md' 'Rating: good'
        Invoke-StateMigrate | Should -Be 4
        $s = (Blob-State).Blobs
        $s.Keys | Sort-Object | Should -Be @('board.json', 'evals/J-001.md', 'events/2026-09.jsonl', 'events/2026-10.jsonl')
        Sync-Board | Should -Be 'same'
        Push-State | Should -Be 0
        { Invoke-StateMigrate } | Should -Throw '*already has a board*'
    }
}

Describe 'cap' {
    It 'rolls off bulky logs of the oldest finished job first, keeps live jobs, evals and lessons, and never re-pushes what it pruned' {
        $big = 'x' * 1000
        Put $script:A 'jobs/J-001/spec.md' 's'
        Put $script:A 'jobs/J-001/run-1.jsonl' $big
        Put $script:A 'jobs/J-001/deploy-1.log' $big
        Put $script:A 'jobs/J-002/run-1.jsonl' $big
        Put $script:A 'jobs/J-003/run-1.jsonl' $big
        Put $script:A 'evals/J-001.md' $big
        Put $script:A 'lessons/J-001.md' $big
        $null = Push-State
        $jobs = @(
            @{ id = 'J-001'; stage = 'done'; updatedAt = '2026-10-01T00:00:00.000Z' }
            @{ id = 'J-002'; stage = 'discarded'; updatedAt = '2026-10-02T00:00:00.000Z' }
            @{ id = 'J-003'; stage = 'build'; updatedAt = '2026-09-01T00:00:00.000Z' }
        )
        $env:FACTORY_STATE_CAP = '4500'
        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        $r = Invoke-StatePrune -Jobs $jobs
        $r.Used | Should -BeLessOrEqual 4500
        $s = (Blob-State).Blobs
        $s.ContainsKey('jobs/J-001/run-1.jsonl') | Should -BeFalse
        $s.ContainsKey('jobs/J-001/deploy-1.log') | Should -BeFalse
        $s.ContainsKey('jobs/J-001/spec.md') | Should -BeTrue
        $s.ContainsKey('jobs/J-002/run-1.jsonl') | Should -BeTrue
        $s.ContainsKey('jobs/J-003/run-1.jsonl') | Should -BeTrue
        $s.ContainsKey('evals/J-001.md') | Should -BeTrue
        (Blob-State).Versions | Where-Object { $_ -like 'jobs/J-001/run-*' } | Should -BeNullOrEmpty
        Test-Path (Join-Path $script:A 'jobs/J-001/run-1.jsonl') | Should -BeTrue
        Push-State | Should -Be 0
    }

    It 'drops old versions before any current blob' {
        Put $script:A 'jobs/J-001/spec.md' ('a' * 1000)
        $null = Push-State
        Put $script:A 'jobs/J-001/spec.md' ('b' * 1001)
        $null = Push-State
        $env:FACTORY_STATE_CAP = '1500'
        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        $r = Invoke-StatePrune -Jobs @(@{ id = 'J-001'; stage = 'done'; updatedAt = '2026-10-01T00:00:00.000Z' })
        $r.Deleted | Should -Be 1
        (Blob-State).Blobs.ContainsKey('jobs/J-001/spec.md') | Should -BeTrue
    }
}

Describe 'token' {
    It 'gets an az token, caches it outside FACTORY_HOME, and asks again only when it nears expiry' {
        Remove-Item Env:FACTORY_STATE_TOKEN
        $log = Join-Path $TestDrive "az-$([guid]::NewGuid().ToString('N')).log"
        $exp = [DateTimeOffset]::UtcNow.AddSeconds(30 * 60).ToUnixTimeSeconds()
        $az = Join-Path $TestDrive 'az-token.ps1'
        Set-Content $az @("Add-Content '$log' (`$args -join ' ')", "@{ accessToken = 'test-token'; expires_on = `$env:AZ_EXP } | ConvertTo-Json")
        $env:FACTORY_AZ_CMD = $az
        $env:AZ_EXP = "$exp"
        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        $null = Write-StateBlob 't' (Bytes 'x')
        $null = Read-StateBlob 't'
        @(Get-Content $log).Count | Should -Be 1
        @(Get-Content $log)[0] | Should -Match 'get-access-token --resource https://storage.azure.com'
        Test-Path (Join-Path $env:FACTORY_LOCAL_DIR 'storage-token.json') | Should -BeTrue
        Get-ChildItem $script:A -Recurse -File | Should -BeNullOrEmpty

        # A new process (Initialize-State again) reuses the cache; a near-expiry one is refreshed.
        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        $null = Read-StateBlob 't'
        @(Get-Content $log).Count | Should -Be 1
        $c = Get-Content (Join-Path $env:FACTORY_LOCAL_DIR 'storage-token.json') -Raw | ConvertFrom-Json
        $c.expiresOn = [DateTimeOffset]::UtcNow.AddSeconds(60).ToUnixTimeSeconds()
        $c | ConvertTo-Json | Set-Content (Join-Path $env:FACTORY_LOCAL_DIR 'storage-token.json')
        Initialize-State -StateHome $script:A -EnvFile $script:EnvFile
        $null = Read-StateBlob 't'
        @(Get-Content $log).Count | Should -Be 2
        Remove-Item Env:AZ_EXP
    }
}

}

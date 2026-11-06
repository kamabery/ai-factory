#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }
# The deploy canary: see "Canary" in docs/FACTORY-CONTRACT.md.
BeforeAll {
    $script:Factory = Join-Path $PSScriptRoot '..\scripts\factory.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    . (Join-Path $PSScriptRoot 'fixtures\github.ps1')
    . (Join-Path $PSScriptRoot 'fixtures\blob.ps1')
    Start-GitHubStub
    Start-BlobStub
    function F { $out = & $script:Factory @args 2>$null; $out }
    function Canary { Get-Content (Join-Path $env:FACTORY_HOME 'canary.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
    function Events { Get-Content (Join-Path $env:FACTORY_HOME 'events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable -DateKind String } }
    function CanaryName { ($script:CanaryRepo -split '/')[1] }
    function CanaryRuns { @((DeployState).runs.Values | Where-Object { $_.repo -eq $script:CanaryRepo }) }
    function CanaryWorkspace { (DeployState).workspaces.Values | Where-Object { $_.name -eq "$(CanaryName)-j000" } | Select-Object -First 1 }
    # The teardown is detached and lands after the canary has exited: waits until it ends and returns its record.
    function Wait-Teardown {
        $deadline = (Get-Date).AddSeconds(45)
        while ($true) {
            $t = (Canary).teardown
            if ($t -and $t.status -ne 'destroying' -and -not (Test-Path (Join-Path $env:FACTORY_HOME 'canary.lock'))) { return $t }
            if ((Get-Date) -gt $deadline) { throw "the canary's teardown never finished: $($t | ConvertTo-Json -Compress)" }
            Start-Sleep -Milliseconds 300
        }
    }
    function New-ApprovedJob([hashtable] $extra = @{}) {
        $idea = F new-idea 'idea' -Target $script:Repo
        $p = Join-Path $TestDrive "spec-$([guid]::NewGuid().ToString('N')).md"
        Set-Content $p "# Spec: Thing`n`n## Target`n- Repo: $script:Repo`n`n## Gate`n- build: ``exit 0```n"
        $id = F add-spec $idea $p
        F approve $id | Out-Null
        $id
    }
}
AfterAll { Stop-GitHubStub; Stop-BlobStub }

Describe 'factory.ps1 canary' {

BeforeEach {
    $script:Repo = "test/repo-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Remote $script:Repo
    $script:CanaryRepo = "test/canary-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $env:FACTORY_HOME = Join-Path $TestDrive "state-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $env:FACTORY_WORK = Join-Path $TestDrive "work-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    Use-Deploys
    $envFile = Join-Path $TestDrive "canary-$([guid]::NewGuid().ToString('N').Substring(0,8)).env"
    Set-Content $envFile ((Get-Content $script:DeployEnvFile) + "FACTORY_CANARY_REPO=$script:CanaryRepo" +
        'FACTORY_ENTRA_TENANT_ID=00000000-0000-0000-0000-0000000000e1' + 'FACTORY_ENTRA_SUBDOMAIN=testpreviews' +
        'FACTORY_ENTRA_USER_FLOW_ID=00000000-0000-0000-0000-0000000000f1')
    $env:FACTORY_ENV_FILE = $envFile

    # The template workspace the fingerprint reads.
    $env:FACTORY_TEMPLATES = Join-Path $TestDrive "templates-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Force "$env:FACTORY_TEMPLATES/IaC", "$env:FACTORY_TEMPLATES/base-project/.github/workflows", "$env:FACTORY_TEMPLATES/artifacts/nupkg" | Out-Null
    Set-Content "$env:FACTORY_TEMPLATES/IaC/providers.tf" 'provider "azurerm" {}'
    Set-Content "$env:FACTORY_TEMPLATES/base-project/.github/workflows/deploy.yml" 'name: deploy'
    Set-Content "$env:FACTORY_TEMPLATES/artifacts/nupkg/AspireStarter.Templates.1.0.0.nupkg" 'pack'

    # Stands in for dotnet new + aspire-addon: a README, and deploy-fail.txt when CANARY_DEPLOY_FAIL is set.
    $env:FACTORY_CANARY_SCAFFOLD_CMD = Join-Path $TestDrive 'scaffold.ps1'
    Set-Content $env:FACTORY_CANARY_SCAFFOLD_CMD @(
        'Set-Content README.md "canary scaffold"'
        'New-Item -ItemType Directory -Force IaC/envs | Out-Null; Set-Content IaC/envs/ephemeral.tfvars ''project_name = "canary"'''
        'if ($env:CANARY_DEPLOY_FAIL) { Set-Content deploy-fail.txt $env:CANARY_DEPLOY_FAIL }'
    )
    $env:FACTORY_SMOKE_SECONDS = '0'
}

AfterEach {
    foreach ($f in 'destroy-fails', 'destroy-hangs') { Remove-Item (Join-Path $script:GitBase $f) -ErrorAction SilentlyContinue }
    # A detached teardown must not outlive the state and stubs it writes to.
    if ((Test-Path (Join-Path $env:FACTORY_HOME 'canary.json')) -and (Canary).Contains('teardown')) { Wait-Teardown | Out-Null }
    Clear-Deploys
    Remove-Item Env:FACTORY_HOME, Env:FACTORY_WORK, Env:FACTORY_ENV_FILE, Env:FACTORY_TEMPLATES, Env:FACTORY_CANARY_SCAFFOLD_CMD,
        Env:FACTORY_SMOKE_SECONDS, Env:CANARY_DEPLOY_FAIL -ErrorAction SilentlyContinue
}

Describe 'canary' {
    It 'scaffolds, deploys j000 from main, passes the smoke check, tears it down and records a pass' {
        $out = F canary
        $LASTEXITCODE | Should -Be 0
        ($out -join "`n") | Should -Match 'canary: pass'

        $c = Canary
        $c.status | Should -Be 'pass'
        $c.fingerprint | Should -Match '^sha256:[0-9a-f]{64}$'
        $c.repo | Should -Be $script:CanaryRepo
        $c.n | Should -Be 1
        $c.urls.api | Should -Match '/j000/api$'
        $c.urls.health | Should -Match '/j000/web/api/health$'
        $c.error | Should -BeNullOrEmpty

        git -C (Bare $script:CanaryRepo) show main:README.md | Should -Be 'canary scaffold'
        $run = CanaryRuns | Select-Object -First 1
        $run.head_branch | Should -Be 'main'
        $run.environment | Should -Be 'j000'
        (Events | Where-Object { $_.kind -eq 'canary' }).detail | Should -Be 'pass'
        Test-Path (Join-Path $env:FACTORY_WORK 'canary') | Should -BeFalse

        (Wait-Teardown).status | Should -Be 'down'
        (CanaryWorkspace).deleted | Should -BeTrue
        (Events | Where-Object { $_.kind -eq 'destroy' -and $_.job -eq 'canary' }).detail | Should -Be 'down'
        Get-Content (Join-Path $env:FACTORY_HOME 'canary-1.log') -Raw | Should -Match '## Destroy'
    }

    It 'records the pass and exits without waiting for the teardown, which takes as long again' {
        $env:FACTORY_DEPLOY_POLL_SECONDS = '1'   # at 0, polling a run that hangs runs the machine out of ports
        Set-Content (Join-Path $script:GitBase 'destroy-hangs') ''
        F canary | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Canary).status | Should -Be 'pass'
        (Canary).teardown.status | Should -Be 'destroying'
        (CanaryWorkspace).deleted | Should -BeFalse

        Remove-Item (Join-Path $script:GitBase 'destroy-hangs')
        (Wait-Teardown).status | Should -Be 'down'
        (Canary).status | Should -Be 'pass'
    }

    It 'a job is released by the pass while the teardown still runs' {
        $id = New-ApprovedJob
        $env:FACTORY_DEPLOY_POLL_SECONDS = '1'
        Set-Content (Join-Path $script:GitBase 'destroy-hangs') ''
        F canary | Out-Null
        F next | Should -Be $id
    }

    It 'a new canary waits for the last one''s teardown, which still holds j000' {
        $env:FACTORY_DEPLOY_POLL_SECONDS = '1'
        Set-Content (Join-Path $script:GitBase 'destroy-hangs') ''
        F canary | Out-Null
        $p = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $script:Factory, 'canary', '-Force' -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 4
        CanaryRuns | Should -HaveCount 1

        Remove-Item (Join-Path $script:GitBase 'destroy-hangs')
        $p.WaitForExit(60000) | Should -BeTrue
        $p.ExitCode | Should -Be 0
        CanaryRuns | Should -HaveCount 2
        (Canary).n | Should -Be 2
    }

    It 'is up to date while the fingerprint has a pass, and -Force runs it again' {
        F canary | Out-Null
        F canary | Should -Be 'canary: up to date'
        $LASTEXITCODE | Should -Be 0
        CanaryRuns | Should -HaveCount 1

        F canary -Force | Out-Null
        $LASTEXITCODE | Should -Be 0
        CanaryRuns | Should -HaveCount 2
        (Canary).n | Should -Be 2
    }

    It 'runs again when a template IaC file changes' {
        F canary | Out-Null
        $before = (Canary).fingerprint
        Set-Content "$env:FACTORY_TEMPLATES/IaC/providers.tf" 'provider "azurerm" { use_oidc = true }'

        F canary | Out-Null
        CanaryRuns | Should -HaveCount 2
        (Canary).fingerprint | Should -Not -Be $before
    }

    It 'runs again when the HCP variable set changes' {
        F canary | Out-Null
        $before = (Canary).fingerprint
        # As the EXTERNAL configuration that broke J-019: a new variable on the shared set.
        Set-Content (Join-Path $script:GitBase 'varset-vars.json') '[{"key":"TFC_AZURE_PROVIDER_AUTH_EXTERNAL","value":"true","category":"env","sensitive":false}]'
        try { F canary | Out-Null } finally { Remove-Item (Join-Path $script:GitBase 'varset-vars.json') }
        CanaryRuns | Should -HaveCount 2
        (Canary).fingerprint | Should -Not -Be $before
    }

    It 'a failed deploy records the first error, writes canary-1.log, still tears down, and exits 1' {
        $env:CANARY_DEPLOY_FAIL = 'exec: "az": executable file not found in $PATH'
        F canary | Out-Null
        $LASTEXITCODE | Should -Be 1

        $c = Canary
        $c.status | Should -Be 'failed'
        $c.error | Should -Be 'Error: exec: "az": executable file not found in $PATH'
        $c.log | Should -Be 'canary-1.log'
        Get-Content (Join-Path $env:FACTORY_HOME 'canary-1.log') -Raw | Should -Match '## GitHub'
        (Wait-Teardown).status | Should -Be 'down'
        (CanaryWorkspace).deleted | Should -BeTrue
        (Events | Where-Object { $_.kind -eq 'canary' }).detail | Should -Be 'failed: Error: exec: "az": executable file not found in $PATH'
    }

    It 'scaffolds with sign-in by default, so every canary proves the sign-in smoke' {
        # The stub is handed the commands a real canary runs.
        Add-Content $env:FACTORY_CANARY_SCAFFOLD_CMD 'Set-Content (Join-Path $env:FACTORY_HOME "scaffold-args.txt") ($args -join "`n")'
        F canary | Out-Null
        $LASTEXITCODE | Should -Be 0
        $cmds = Get-Content (Join-Path $env:FACTORY_HOME 'scaffold-args.txt')
        $cmds | Should -Contain 'aspire-addon add ui-react'
        $cmds | Should -Contain 'aspire-addon add auth'
    }

    It 'writes the Entra External ID ids from .env into the scaffold''s ephemeral.tfvars (canary 15)' {
        F canary | Out-Null
        $LASTEXITCODE | Should -Be 0
        $tfvars = git -C (Bare $script:CanaryRepo) show main:IaC/envs/ephemeral.tfvars
        $tfvars | Should -Contain 'project_name = "canary"'
        $tfvars | Should -Contain 'entra_external_tenant_id    = "00000000-0000-0000-0000-0000000000e1"'
        $tfvars | Should -Contain 'entra_external_subdomain    = "testpreviews"'
        $tfvars | Should -Contain 'entra_external_user_flow_id = "00000000-0000-0000-0000-0000000000f1"'
    }

    It 'runs on the shared platform by default, as a real job''s preview does' {
        F canary | Out-Null
        $LASTEXITCODE | Should -Be 0
        git -C (Bare $script:CanaryRepo) show main:IaC/envs/ephemeral.tfvars | Should -Not -Contain 'use_platform = false'
        (Canary).full | Should -BeFalse
    }

    It '-Full builds the whole stack, which is what still proves a from-scratch create, and runs even when up to date' {
        F canary | Out-Null
        F canary -Full | Out-Null
        $LASTEXITCODE | Should -Be 0
        CanaryRuns | Should -HaveCount 2
        git -C (Bare $script:CanaryRepo) show main:IaC/envs/ephemeral.tfvars | Should -Contain 'use_platform = false'
        (Canary).full | Should -BeTrue
    }

    It 'refuses a scaffold with auth when .env lacks the Entra ids, before anything deploys' {
        Set-Content $env:FACTORY_ENV_FILE ((Get-Content $script:DeployEnvFile) + "FACTORY_CANARY_REPO=$script:CanaryRepo")
        F canary | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        @(CanaryRuns).Count | Should -Be 0
    }

    It 'with a QA user on j000 (a scaffold with auth), a failed sign-in smoke fails the canary, and it still tears down' {
        $env:FACTORY_SIGNIN_LOG = Join-Path $TestDrive "signin-$([guid]::NewGuid().ToString('N').Substring(0,8)).jsonl"
        $env:FACTORY_SIGNIN_EXIT = '1'
        F canary | Out-Null
        $LASTEXITCODE | Should -Be 1
        (Canary).error | Should -Match 'AADSTS500011'
        $log = Get-Content (Join-Path $env:FACTORY_HOME 'canary-1.log') -Raw
        $log | Should -Match '## Sign-in smoke'
        $log | Should -Not -Match 'pw-j000'
        ((Get-Content $env:FACTORY_SIGNIN_LOG | ConvertFrom-Json).args -join ' ') | Should -Match '/j000/web'
        (Wait-Teardown).status | Should -Be 'down'
        (CanaryWorkspace).deleted | Should -BeTrue
    }

    It 'without a QA user on j000, the canary passes with no sign-in smoke' {
        $env:FACTORY_SIGNIN_LOG = Join-Path $TestDrive "signin-$([guid]::NewGuid().ToString('N').Substring(0,8)).jsonl"
        $o, $n = $script:CanaryRepo -split '/'
        Set-Content (Join-Path $script:GitBase "$o/$n.noauth") ''
        F canary | Out-Null
        $LASTEXITCODE | Should -Be 0
        Test-Path $env:FACTORY_SIGNIN_LOG | Should -BeFalse
    }

    It 'an API whose /health is not 200 fails the smoke check' {
        $o, $n = $script:CanaryRepo -split '/'
        Set-Content (Join-Path $script:GitBase "$o/$n.unhealthy") ''
        F canary | Out-Null
        $LASTEXITCODE | Should -Be 1
        (Canary).error | Should -Match '^smoke: .*/j000/web/api/health answered 503'
        Get-Content (Join-Path $env:FACTORY_HOME 'canary-1.log') -Raw | Should -Match '## Smoke'
        # canary-7: an empty-bodied 400 from /health; the Server header says whether the ingress or the app sent it.
        Get-Content (Join-Path $env:FACTORY_HOME 'canary-1.log') -Raw | Should -Match '/j000/web/api/health answered 503 in \d+s, server: \S+'
        (Wait-Teardown).status | Should -Be 'down'
        (CanaryWorkspace).deleted | Should -BeTrue
    }

    It 'a teardown that errors keeps the pass, records HCP''s own error, and keeps the workspace for sweep to retry' {
        # canary-8: the log said only "destroy run ended errored"; the reason was in HCP's apply log.
        Set-Content (Join-Path $script:GitBase 'destroy-fails') ''
        F canary | Out-Null
        $LASTEXITCODE | Should -Be 0
        $t = Wait-Teardown
        (Canary).status | Should -Be 'pass'
        $t.status | Should -Be 'failed'
        $t.failures | Should -Be 1
        $t.error | Should -Be 'destroy run ended errored: Error: deleting Resource Group "rg-x": the Resource Group still contains Resources.'
        Get-Content (Join-Path $env:FACTORY_HOME 'canary-1.log') -Raw | Should -Match '(?s)## Destroy.*still contains Resources'
        (CanaryWorkspace).deleted | Should -BeFalse

        Remove-Item (Join-Path $script:GitBase 'destroy-fails')
        F sweep | Should -Be 'canary'
        $deadline = (Get-Date).AddSeconds(30)
        while ((Canary).teardown.status -ne 'down' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
        (Wait-Teardown).status | Should -Be 'down'
        (Wait-Teardown).failures | Should -Be 0
        (CanaryWorkspace).deleted | Should -BeTrue
    }

    It 'refuses while another canary holds canary.lock' {
        New-Item -ItemType Directory -Force $env:FACTORY_HOME | Out-Null
        Set-Content (Join-Path $env:FACTORY_HOME 'canary.lock') $PID
        F canary | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        CanaryRuns | Should -HaveCount 0
    }
}

Describe 'canary on azure' {
    BeforeEach { Use-AzureState }
    AfterEach { Clear-AzureState }

    It 'refuses while another machine holds locks/canary, and frees the lease after a run' {
        . (Join-Path $PSScriptRoot '..\scripts\state.ps1')
        Initialize-State -StateHome (Join-Path $TestDrive 'elsewhere') -EnvFile $null
        $held = Enter-StateLease 'canary' 5
        try {
            F canary | Out-Null
            $LASTEXITCODE | Should -Not -Be 0
            CanaryRuns | Should -HaveCount 0
            Test-Path (Join-Path $env:FACTORY_HOME 'canary.lock') | Should -BeFalse
        } finally { Exit-StateLease 'canary' $held }

        F canary | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Canary).status | Should -Be 'pass'
        Wait-Teardown | Out-Null
        (Blob-State).Blobs['locks/canary'].leased | Should -BeFalse
        Blob-Text 'canary.json' | Should -Match '"pass"'
    }
}

Describe 'next and the canary' {
    It 'holds back a deployable job until a canary has passed for the current fingerprint' {
        $id = New-ApprovedJob
        F next | Should -BeNullOrEmpty
        F next -NeedsCanary | Should -Be 'canary'

        F canary | Out-Null
        F next | Should -Be $id
        F next -NeedsCanary | Should -Be $id
    }

    It 'holds it back again after a failed canary' {
        $id = New-ApprovedJob
        $env:CANARY_DEPLOY_FAIL = 'boom'
        F canary | Out-Null
        F next | Should -BeNullOrEmpty
        F next -NeedsCanary | Should -BeNullOrEmpty
    }

    It 'does not hold back a bootstrap job' {
        $out = F new-idea 'boot' -Target "test/boot-$([guid]::NewGuid().ToString('N').Substring(0,8))" -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false}'
        $id = ($out -split ' ')[1]
        F approve $id | Out-Null
        F next | Should -Be $id
    }

    It 'with deploys off, needs no canary' {
        $env:FACTORY_ENV_FILE = $script:EnvFile
        $id = New-ApprovedJob
        F next | Should -Be $id
    }
}

}

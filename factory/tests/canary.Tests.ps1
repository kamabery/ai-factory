#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }
# The deploy canary: see "Canary" in docs/FACTORY-CONTRACT.md.
BeforeAll {
    $script:Factory = Join-Path $PSScriptRoot '..\scripts\factory.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    . (Join-Path $PSScriptRoot 'fixtures\github.ps1')
    Start-GitHubStub
    function F { $out = & $script:Factory @args 2>$null; $out }
    function Canary { Get-Content (Join-Path $env:FACTORY_HOME 'canary.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
    function Events { Get-Content (Join-Path $env:FACTORY_HOME 'events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable -DateKind String } }
    function CanaryName { ($script:CanaryRepo -split '/')[1] }
    function CanaryRuns { @((DeployState).runs.Values | Where-Object { $_.repo -eq $script:CanaryRepo }) }
    function CanaryWorkspace { (DeployState).workspaces.Values | Where-Object { $_.name -eq "$(CanaryName)-j000" } | Select-Object -First 1 }
    function New-ApprovedJob([hashtable] $extra = @{}) {
        $idea = F new-idea 'idea' -Target $script:Repo
        $p = Join-Path $TestDrive "spec-$([guid]::NewGuid().ToString('N')).md"
        Set-Content $p "# Spec: Thing`n`n## Target`n- Repo: $script:Repo`n`n## Gate`n- build: ``exit 0```n"
        $id = F add-spec $idea $p
        F approve $id | Out-Null
        $id
    }
}
AfterAll { Stop-GitHubStub }

Describe 'factory.ps1 canary' {

BeforeEach {
    $script:Repo = "test/repo-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Remote $script:Repo
    $script:CanaryRepo = "test/canary-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $env:FACTORY_HOME = Join-Path $TestDrive "state-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $env:FACTORY_WORK = Join-Path $TestDrive "work-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    Use-Deploys
    $envFile = Join-Path $TestDrive "canary-$([guid]::NewGuid().ToString('N').Substring(0,8)).env"
    Set-Content $envFile ((Get-Content $script:DeployEnvFile) + "FACTORY_CANARY_REPO=$script:CanaryRepo")
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
        'if ($env:CANARY_DEPLOY_FAIL) { Set-Content deploy-fail.txt $env:CANARY_DEPLOY_FAIL }'
    )
    $env:FACTORY_SMOKE_SECONDS = '0'
}

AfterEach {
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
        (CanaryWorkspace).deleted | Should -BeTrue
        (Events | Where-Object { $_.kind -eq 'canary' }).detail | Should -Be 'pass'
        Test-Path (Join-Path $env:FACTORY_WORK 'canary') | Should -BeFalse
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
        (CanaryWorkspace).deleted | Should -BeTrue
        (Events | Where-Object { $_.kind -eq 'canary' }).detail | Should -Be 'failed: Error: exec: "az": executable file not found in $PATH'
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
        (CanaryWorkspace).deleted | Should -BeTrue
    }

    It 'a destroy that errors fails the canary with HCP''s own error, and keeps the workspace' {
        # canary-8: the log said only "destroy run ended errored"; the reason was in HCP's apply log.
        Set-Content (Join-Path $script:GitBase 'destroy-fails') ''
        try { F canary | Out-Null } finally { Remove-Item (Join-Path $script:GitBase 'destroy-fails') }
        $LASTEXITCODE | Should -Be 1
        (Canary).error | Should -Be 'destroy: destroy run ended errored: Error: deleting Resource Group "rg-x": the Resource Group still contains Resources.'
        Get-Content (Join-Path $env:FACTORY_HOME 'canary-1.log') -Raw | Should -Match '(?s)## Destroy.*still contains Resources'
        (CanaryWorkspace).deleted | Should -BeFalse
    }

    It 'refuses while another canary holds canary.lock' {
        New-Item -ItemType Directory -Force $env:FACTORY_HOME | Out-Null
        Set-Content (Join-Path $env:FACTORY_HOME 'canary.lock') $PID
        F canary | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
        CanaryRuns | Should -HaveCount 0
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

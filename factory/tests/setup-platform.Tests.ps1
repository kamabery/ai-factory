#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }
# factory/scripts/setup-platform.ps1: the shared platform previews run on. See docs/DEPLOY.md.

BeforeAll {
    $script:Setup = Join-Path $PSScriptRoot '..\scripts\setup-platform.ps1' | Resolve-Path | Select-Object -ExpandProperty Path
    . (Join-Path $PSScriptRoot 'fixtures\github.ps1')
    Start-GitHubStub

    function Calls { Get-Content $env:FACTORY_TOOL_LOG | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } }
    function Matching([string] $pattern) { @(Calls | Where-Object { ($_.args -join ' ') -match $pattern }) }
    function Setup { & $script:Setup -EnvFile $script:DeployEnvFile @args 6>$null }
    function Arg($call, [string] $name) { $a = $call.args; $a[[array]::IndexOf($a, $name) + 1] }
    function Varset { $f = Join-Path $script:GitBase 'varset-vars.json'; if (Test-Path $f) { @(Get-Content $f -Raw | ConvertFrom-Json) } else { @() } }
}
AfterAll { Stop-GitHubStub }

Describe 'setup-platform.ps1' {
    BeforeEach {
        $env:FACTORY_AZ_CMD = Join-Path $PSScriptRoot 'fixtures\az-platform-stub.ps1'
        $env:FACTORY_TOOL_LOG = Join-Path $TestDrive "az-$([guid]::NewGuid().ToString('N')).jsonl"
        $env:FACTORY_AZ_STUB = 'empty'
        $env:FACTORY_SETUP_POLL_SECONDS = '0'
        Remove-Item (Join-Path $script:GitBase 'varset-vars.json') -ErrorAction SilentlyContinue
    }
    AfterAll { Remove-Item Env:FACTORY_AZ_CMD, Env:FACTORY_TOOL_LOG, Env:FACTORY_AZ_STUB, Env:FACTORY_SETUP_POLL_SECONDS, Env:FACTORY_AZ_STUB_SECRET_FAILS -ErrorAction SilentlyContinue }

    It 'creates the environment, Log Analytics, vault and Postgres server in the previews'' region' {
        Setup -SubscriptionId sub-1 | Out-Null
        foreach ($p in '^group create', '^monitor log-analytics workspace create', '^containerapp env create', '^keyvault create', '^postgres flexible-server create') {
            $c = @(Matching $p)
            @($c).Count | Should -Be 1 -Because $p
            Arg @($c)[0] '--location' | Should -Be 'centralus'
        }
    }

    It 'sizes the server as the template does, with the Azure services rule, and caps the logs near the free 5 GB' {
        Setup -SubscriptionId sub-1 | Out-Null
        $pg = @(Matching '^postgres flexible-server create')[0]
        Arg $pg '--tier' | Should -Be 'Burstable'
        Arg $pg '--sku-name' | Should -Be 'Standard_B1ms'
        Arg $pg '--storage-size' | Should -Be '32'
        Arg $pg '--version' | Should -Be '16'
        Arg $pg '--admin-user' | Should -Be 'pgadmin'
        Arg $pg '--public-access' | Should -Be '0.0.0.0'
        Arg (@(Matching '^monitor log-analytics workspace create')[0]) '--quota' | Should -Be '0.2'
    }

    It 'names the vault and server after the subscription, the same on every run, since both names are global' {
        Setup -SubscriptionId sub-1 | Out-Null
        $first = Arg (@(Matching '^keyvault create')[0]) '--name'
        $first | Should -Match '^kv-factory-[0-9a-f]{8}$'
        Arg (@(Matching '^postgres flexible-server create')[0]) '--name' | Should -Match '^psql-factory-[0-9a-f]{8}$'
        Remove-Item $env:FACTORY_TOOL_LOG
        Setup -SubscriptionId sub-1 | Out-Null
        Arg (@(Matching '^keyvault create')[0]) '--name' | Should -Be $first
    }

    It 'grants HCP Secrets Officer and GitHub Actions Secrets User on the vault, and no preview-wide more' {
        Setup -SubscriptionId sub-1 | Out-Null
        $roles = Matching '^role assignment create' | ForEach-Object { "$(Arg $_ '--assignee-object-id') $(Arg $_ '--role') $(Arg $_ '--scope')" }
        $roles | Should -Contain 'sp-hcp Key Vault Secrets Officer /subscriptions/sub-1/kv'
        $roles | Should -Contain 'sp-gh Key Vault Secrets User /subscriptions/sub-1/kv'
        $roles | Should -Contain 'user-me Key Vault Secrets Officer /subscriptions/sub-1/kv'
    }

    It 'stores the admin password in the vault under the name deploy.yml reads, and never prints it' {
        $out = Setup -SubscriptionId sub-1
        $set = @(Matching '^keyvault secret set')[0]
        Arg $set '--name' | Should -Be 'postgres-admin-password'
        $pw = Arg $set '--value'
        $pw | Should -Match '^[A-Za-z0-9]{32,}$'
        Arg (@(Matching '^postgres flexible-server create')[0]) '--admin-password' | Should -Be $pw
        ($out -join "`n") | Should -Not -Match $pw
    }

    It 'waits for the vault role to land before giving up on the secret' {
        $env:FACTORY_AZ_STUB_SECRET_FAILS = Join-Path $TestDrive 'secret-fails'
        Set-Content $env:FACTORY_AZ_STUB_SECRET_FAILS '2'
        try { Setup -SubscriptionId sub-1 | Out-Null }
        finally { Remove-Item Env:FACTORY_AZ_STUB_SECRET_FAILS }
        @(Matching '^keyvault secret set').Count | Should -Be 3
        @(Matching '^postgres flexible-server create').Count | Should -Be 1
    }

    It 'does not put the password in a failure message' {
        $env:FACTORY_AZ_STUB_SECRET_FAILS = Join-Path $TestDrive 'secret-fails'
        Set-Content $env:FACTORY_AZ_STUB_SECRET_FAILS '999'
        try { $msg = try { Setup -SubscriptionId sub-1 | Out-Null; '' } catch { "$_" } }
        finally { Remove-Item Env:FACTORY_AZ_STUB_SECRET_FAILS }
        $msg | Should -Match 'Forbidden'
        $pw = Arg (@(Matching '^keyvault secret set')[0]) '--value'
        $msg | Should -Not -Match $pw
    }

    It 'puts the platform on the HCP variable set as an HCL terraform variable, which the template reads' {
        Setup -SubscriptionId sub-1 | Out-Null
        $v = Varset | Where-Object key -eq 'platform'
        $v.category | Should -Be 'terraform'
        $v.hcl | Should -BeTrue
        $v.sensitive | Should -BeFalse
        foreach ($expected in 'location *= *"centralus"', 'environment_id *= *"/subscriptions/sub-1/cae"', 'environment_name *= *"cae-factory-platform"',
                'environment_domain *= *"calm.centralus.azurecontainerapps.io"', 'log_analytics_workspace_id *= *"/subscriptions/sub-1/law"',
                'key_vault_id *= *"/subscriptions/sub-1/kv"', 'key_vault_name *= *"kv-factory-', 'postgres_server_id *= *"/subscriptions/sub-1/psql"',
                'postgres_server_name *= *"psql-factory-', 'postgres_server_fqdn *= *"psql-x.postgres.database.azure.com"', 'postgres_resource_group *= *"rg-factory-platform"') {
            $v.value | Should -Match $expected
        }
    }

    It 'run again creates nothing, keeps the password, and leaves the variable set as it was' {
        Setup -SubscriptionId sub-1 | Out-Null
        $before = (Varset | Where-Object key -eq 'platform')

        $env:FACTORY_AZ_STUB = 'exists'
        Remove-Item $env:FACTORY_TOOL_LOG
        Setup -SubscriptionId sub-1 | Out-Null

        foreach ($p in 'workspace create', 'env create', '^keyvault create', 'flexible-server create', 'secret set', 'role assignment create') {
            @(Matching $p).Count | Should -Be 0 -Because $p
        }
        @(Varset | Where-Object key -eq 'platform').Count | Should -Be 1
        (Varset | Where-Object key -eq 'platform').value | Should -Be $before.value
    }

    It 'updates the variable on the set when the platform changed' {
        Set-Content (Join-Path $script:GitBase 'varset-vars.json') '[{"key":"platform","value":"{ old = true }","category":"terraform","hcl":true,"sensitive":false}]'
        Setup -SubscriptionId sub-1 | Out-Null
        @(Varset | Where-Object key -eq 'platform').Count | Should -Be 1
        (Varset | Where-Object key -eq 'platform').value | Should -Match 'cae-factory-platform'
    }

    It 'says to run setup-azure.ps1 first when its identities are missing' {
        $env:FACTORY_AZ_CMD = Join-Path $TestDrive 'no-identities.ps1'
        Set-Content $env:FACTORY_AZ_CMD '$l = $args -join " "; if ($l -match "^ad sp list") { exit 0 }; if ($l -match "^account show") { "sub-1" }; exit 0'
        { Setup -SubscriptionId sub-1 } | Should -Throw '*setup-azure.ps1*'
    }
}

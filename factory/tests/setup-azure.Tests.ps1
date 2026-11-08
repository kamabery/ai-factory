#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

BeforeAll {
    $script:Setup = Join-Path $PSScriptRoot '..\scripts\setup-azure.ps1' | Resolve-Path | Select-Object -ExpandProperty Path

    function Calls { Get-Content $env:FACTORY_TOOL_LOG | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } }
    function Matching([string] $pattern) { @(Calls | Where-Object { ($_.args -join ' ') -match $pattern }) }
    function Setup { & $script:Setup -EnvFile $script:EnvFile @args 6>$null }
}

Describe 'setup-azure.ps1' {
    BeforeEach {
        $env:FACTORY_AZ_CMD = Join-Path $PSScriptRoot 'fixtures\az-setup-stub.ps1'
        $env:FACTORY_TOOL_LOG = Join-Path $TestDrive "az-$([guid]::NewGuid().ToString('N')).jsonl"
        $env:FACTORY_AZ_STUB = 'empty'
        $script:EnvFile = Join-Path $TestDrive "env-$([guid]::NewGuid().ToString('N'))"
        Set-Content $script:EnvFile @('GITHUB_TOKEN=gh-x', '# AZURE_GH_CLIENT_ID=', '# AZURE_TENANT_ID=', '# AZURE_SUBSCRIPTION_ID=')
    }

    AfterAll { Remove-Item Env:FACTORY_AZ_CMD, Env:FACTORY_TOOL_LOG, Env:FACTORY_AZ_STUB -ErrorAction SilentlyContinue }

    It 'creates both main-tenant apps with their service principals and roles' {
        Setup -SubscriptionId sub-1 -HcpOrg acme | Out-Null
        (Matching '^ad app create').ForEach({ $_.args[4] }) | Should -Be @('factory-github-actions', 'factory-hcp-terraform')
        (Matching '^ad sp create').Count | Should -Be 2
        $roles = Matching '^role assignment create' | ForEach-Object { $a = $_.args -join ' '; "$($a -replace '.*--assignee-object-id (\S+).*', '$1') $($a -replace '.*--role (.+?) --scope.*', '$1')" }
        $roles | Should -Be @('sp-app-factory-github-actions Contributor', 'sp-app-factory-hcp-terraform Contributor', 'sp-app-factory-hcp-terraform User Access Administrator')
        Matching '^role assignment create' | ForEach-Object { $_.args -join ' ' | Should -Match '--scope /subscriptions/sub-1' }
    }

    It 'gives the HCP app a flexible federated credential for every workspace and run phase in the org' {
        Setup -SubscriptionId sub-1 -HcpOrg acme | Out-Null
        $fic = @(Matching '^rest --method POST .*applications/obj-factory-hcp-terraform/federatedIdentityCredentials')
        $fic.Count | Should -Be 1
        $b = $fic[0].body | ConvertFrom-Json
        $b.issuer | Should -Be 'https://app.terraform.io'
        $b.audiences | Should -Be 'api://AzureADTokenExchange'
        $b.claimsMatchingExpression.value | Should -Be "claims['sub'] matches 'organization:acme:project:*:workspace:*:run_phase:*'"
    }

    It 'writes the Azure values into .env over the commented lines and keeps the rest' {
        Setup -SubscriptionId sub-1 -HcpOrg acme | Out-Null
        Get-Content $script:EnvFile | Should -Be @('GITHUB_TOKEN=gh-x', 'AZURE_GH_CLIENT_ID=app-factory-github-actions',
            'AZURE_TENANT_ID=tenant-main', 'AZURE_SUBSCRIPTION_ID=sub-1')
    }

    It 'prints the HCP variable set values' {
        $out = Setup -SubscriptionId sub-1 -HcpOrg acme -ExternalTenantId ext-1 -ExternalSubdomain extsub
        $out | Should -Contain 'TFC_AZURE_RUN_CLIENT_ID=app-factory-hcp-terraform'
        $out | Should -Contain 'ARM_SUBSCRIPTION_ID=sub-1'
        $out | Should -Contain 'ARM_TENANT_ID=tenant-main'
        $out | Should -Contain 'TFC_AZURE_RUN_CLIENT_ID_EXTERNAL=app-factory-hcp-terraform'
    }

    It 'prints the entra_* ids as tfvars lines for the target repo, not as variable set values' {
        # J-022: the ids live in the target's IaC/envs/*.tfvars, one source; a -var-file overrides the set.
        $out = Setup -SubscriptionId sub-1 -HcpOrg acme -ExternalTenantId ext-1 -ExternalSubdomain extsub
        $out | Should -Contain 'entra_external_tenant_id    = "ext-1"'
        $out | Should -Contain 'entra_external_subdomain    = "extsub"'
        $out | Should -Contain 'entra_external_user_flow_id = "flow-1"'
        $out -join "`n" | Should -Not -Match 'entra_external_\w+='
    }

    It 'registers the resource providers the templates deploy to, Microsoft.App among them' {
        # canary-6: creating the Container Apps environment failed with MissingSubscriptionRegistration.
        Setup -SubscriptionId sub-1 -HcpOrg acme | Out-Null
        $ns = Matching '^provider register' | ForEach-Object { $a = $_.args; $a[[array]::IndexOf($a, '--namespace') + 1] }
        foreach ($n in 'Microsoft.App', 'Microsoft.ContainerRegistry', 'Microsoft.KeyVault', 'Microsoft.DBforPostgreSQL', 'Microsoft.Web', 'Microsoft.ServiceBus') {
            $ns | Should -Contain $n
        }
        Matching '^provider register' | ForEach-Object { $_.args -join ' ' | Should -Match '--subscription sub-1' }
    }

    It 'prints deployer_object_id, the GitHub Actions identity each preview''s Key Vault lets read its secrets' {
        # canary-4: the schema step's `az keyvault secret show` was ForbiddenByRbac for this identity.
        $out = Setup -SubscriptionId sub-1 -HcpOrg acme
        $out | Should -Contain 'deployer_object_id=sp-app-factory-github-actions'
    }

    It 'sets up the External tenant: user flow, HCP app, Graph roles and federated credential' {
        Setup -SubscriptionId sub-1 -HcpOrg acme -ExternalTenantId ext-1 -ExternalSubdomain extsub | Out-Null
        Matching '^account set --subscription ext-1' | Should -HaveCount 1
        $flow = @(Matching '^rest --method POST .*identity/authenticationEventsFlows')
        $flow | Should -HaveCount 1
        ($flow[0].body | ConvertFrom-Json).onAuthenticationMethodLoadStart.identityProviders.id | Should -Be 'EmailPassword-OAUTH'
        $grants = Matching '^rest --method POST .*servicePrincipals/sp-app-factory-hcp-terraform/appRoleAssignments'
        $grants.ForEach({ ($_.body | ConvertFrom-Json).appRoleId }) | Should -Be @('r-app', 'r-user', 'r-ev', 'r-grant')
        $grants.ForEach({ ($_.body | ConvertFrom-Json).resourceId }) | Select-Object -Unique | Should -Be 'graph-sp'
        Matching '^rest --method POST .*federatedIdentityCredentials' | Should -HaveCount 2
    }

    It "reads the External subdomain from the tenant's initial domain when not passed" {
        $out = Setup -SubscriptionId sub-1 -HcpOrg acme -ExternalTenantId ext-1
        $out | Should -Contain 'entra_external_subdomain    = "extinit"'
    }

    It 'leaves az on the subscription it started on' {
        Setup -SubscriptionId sub-1 -HcpOrg acme -ExternalTenantId ext-1 -ExternalSubdomain extsub | Out-Null
        (Matching '^account set')[-1].args -join ' ' | Should -Be 'account set --subscription sub-orig'
    }

    It 'skips the External tenant without -ExternalTenantId' {
        $out = Setup -SubscriptionId sub-1 -HcpOrg acme
        Matching 'authenticationEventsFlows|ext-1' | Should -HaveCount 0
        $out -join "`n" | Should -Not -Match 'entra_external'
    }

    It 'creates nothing when everything already exists' {
        $env:FACTORY_AZ_STUB = 'full'
        Setup -SubscriptionId sub-1 -HcpOrg acme -ExternalTenantId ext-1 -ExternalSubdomain extsub | Out-Null
        Matching ' create|--method POST' | Should -HaveCount 0
    }

    It 'reads the subscription and HCP org from .env when not passed' {
        Set-Content $script:EnvFile @('AZURE_SUBSCRIPTION_ID=sub-env', 'HCP_ORG=org-env')
        Setup | Out-Null
        Matching '^account set --subscription sub-env' | Should -HaveCount 1
        @(Matching '^rest --method POST .*federatedIdentityCredentials')[0].body | Should -Match 'organization:org-env:'
    }

    It 'creates the External tenant through Resource Manager, signs in to it and sets it up' {
        $env:FACTORY_SETUP_POLL_SECONDS = '0'
        $out = Setup -SubscriptionId sub-1 -HcpOrg acme -CreateExternalTenant newsub -ExternalResourceGroup rg-x
        $put = @(Matching '^rest --method PUT')
        $put | Should -HaveCount 1
        $put[0].args -join ' ' | Should -Match '/subscriptions/sub-1/resourceGroups/rg-x/providers/Microsoft.AzureActiveDirectory/ciamDirectories/newsub.onmicrosoft.com\?api-version='
        $b = $put[0].body | ConvertFrom-Json
        $b.location | Should -Be 'United States'
        $b.sku.name | Should -Be 'Base'
        $b.properties.createTenantProperties.countryCode | Should -Be 'US'
        Matching '^login --tenant ext-new --allow-no-subscriptions' | Should -HaveCount 1
        $out | Should -Contain 'entra_external_tenant_id    = "ext-new"'
        $out | Should -Contain 'entra_external_subdomain    = "newsub"'
        Matching '^rest --method POST .*authenticationEventsFlows' | Should -HaveCount 1
    }

    It 'reuses an External tenant that already exists' {
        $env:FACTORY_AZ_STUB = 'full'
        $out = Setup -SubscriptionId sub-1 -HcpOrg acme -CreateExternalTenant newsub -ExternalResourceGroup rg-x
        Matching '^rest --method PUT' | Should -HaveCount 0
        $out | Should -Contain 'entra_external_tenant_id    = "ext-new"'
    }

    It 'stops rather than write a blank tenant id into .env' {
        $env:FACTORY_AZ_STUB = 'notenant'
        { Setup -SubscriptionId sub-1 -HcpOrg acme } | Should -Throw '*tenant*'
        Get-Content $script:EnvFile | Should -Contain '# AZURE_TENANT_ID='
    }

    It 'runs the az executable on PATH when FACTORY_AZ_CMD is unset' {
        $bin = Join-Path $TestDrive "bin-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory $bin | Out-Null
        Set-Content (Join-Path $bin 'az.cmd') "@pwsh -NoProfile -File `"$env:FACTORY_AZ_CMD`" %*"
        $stub, $path = $env:FACTORY_AZ_CMD, $env:PATH
        try {
            $env:FACTORY_AZ_CMD = $null
            $env:PATH = "$bin;$path"
            Setup -SubscriptionId sub-1 -HcpOrg acme | Out-Null
        } finally { $env:FACTORY_AZ_CMD, $env:PATH = $stub, $path }
        (Matching '^ad app create').Count | Should -Be 2
    }

    It 'refuses without an HCP org' {
        { Setup -SubscriptionId sub-1 } | Should -Throw '*HcpOrg*'
    }
}

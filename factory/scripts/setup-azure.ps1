# One-time Azure setup for ephemeral previews; see docs/DEPLOY.md. In the subscription's tenant: the
# app registrations GitHub Actions and HCP Terraform sign in as, their roles, and HCP's federated
# credential. With -CreateExternalTenant <subdomain>, the Entra External ID tenant is created through
# Resource Manager (the signed-in user becomes its admin); with it or -ExternalTenantId, in that tenant:
# the sign-in user flow and an HCP app with its Graph roles and federated credential. Everything is
# looked up before it is created, so re-running is safe. Writes AZURE_* into .env and prints the
# values for the HCP variable set. Sign az in to the subscription's tenant first; the script signs in
# to the External tenant itself when it has to.
param(
    [string] $SubscriptionId,
    [string] $HcpOrg,
    [string] $ExternalTenantId,
    [string] $ExternalSubdomain,
    [string] $CreateExternalTenant,
    [string] $ExternalResourceGroup,
    [string] $ExternalLocation = 'United States',
    [string] $ExternalCountryCode = 'US',
    [string] $UserFlowName = 'preview-signin',
    [string] $EnvFile = (Join-Path $PSScriptRoot '..\..\.env')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env-file.ps1')

$vars = Read-EnvFile $EnvFile
if (-not $SubscriptionId) { $SubscriptionId = $vars['AZURE_SUBSCRIPTION_ID'] }
if (-not $HcpOrg) { $HcpOrg = $vars['HCP_ORG'] }
if (-not $SubscriptionId) { throw 'Pass -SubscriptionId, or set AZURE_SUBSCRIPTION_ID in .env' }
if (-not $HcpOrg) { throw 'Pass -HcpOrg (the HCP Terraform organization the federated credentials trust)' }

$GraphAppId = '00000003-0000-0000-c000-000000000000'
$HcpGraphRoles = 'Application.ReadWrite.All', 'User.ReadWrite.All', 'EventListener.ReadWrite.All'

# az, or the script FACTORY_AZ_CMD names in its place. Returns stdout trimmed; throws on failure.
# The executable is resolved explicitly: a bare `az` here would call this function.
$AzExe = if (-not $env:FACTORY_AZ_CMD) { (Get-Command az -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
function Az {
    $all = if ($env:FACTORY_AZ_CMD) { & pwsh -NoProfile -File $env:FACTORY_AZ_CMD @args 2>&1 } else { & $AzExe @args 2>&1 }
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "az $($args -join ' ') failed: $(($all | Out-String).Trim())" }
    (@($all | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }) -join "`n").Trim()
}
function AzJson { $o = Az @args -o json; if ($o) { $o | ConvertFrom-Json } }

# Microsoft Graph through az rest. The body goes through a file: az's quoting of inline JSON on
# Windows is unreliable.
function Graph([string] $method, [string] $path, $body = $null) {
    $a = @('rest', '--method', $method, '--uri', "https://graph.microsoft.com$path")
    $file = $null
    if ($null -ne $body) {
        $file = New-TemporaryFile
        $body | ConvertTo-Json -Depth 12 | Set-Content $file -Encoding utf8NoBOM
        $a += '--headers', 'Content-Type=application/json', '--body', "@$file"
    }
    try { $o = Az @a } finally { if ($file) { Remove-Item $file } }
    if ($o) { $o | ConvertFrom-Json }
}

function Use-Account([string] $id, [string] $login) {
    try { Az account set --subscription $id | Out-Null }
    catch { throw "az isn't signed in to $id. Run: $login" }
}

# A tenant with no subscription is selected by its tenant id; az signs in to it on first use.
function Use-Tenant([string] $tenantId) {
    try { Az account set --subscription $tenantId | Out-Null }
    catch {
        Write-Host "  signing in to $tenantId (a browser window opens)"
        Az login --tenant $tenantId --allow-no-subscriptions | Out-Null
        Az account set --subscription $tenantId | Out-Null
    }
}

# The External tenant <subdomain>.onmicrosoft.com as an Azure resource, created if missing; returns its
# tenant id once provisioned (creation can take minutes).
function Ensure-ExternalTenant([string] $subdomain) {
    if (-not $ExternalResourceGroup) { throw 'Pass -ExternalResourceGroup: the resource group the External tenant resource lives in' }
    $uri = "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ExternalResourceGroup/providers/" +
        "Microsoft.AzureActiveDirectory/ciamDirectories/$subdomain.onmicrosoft.com?api-version=2023-05-17-preview"
    $get = { try { Az rest --method GET --uri $uri | ConvertFrom-Json } catch { if ("$_" -match 'ResourceNotFound') { $null } else { throw } } }
    $t = & $get
    if (-not $t) {
        Write-Host "  External tenant $subdomain.onmicrosoft.com: creating"
        $file = New-TemporaryFile
        @{ location = $ExternalLocation; sku = @{ name = 'Base'; tier = 'A0' }
           properties = @{ createTenantProperties = @{ displayName = $subdomain; countryCode = $ExternalCountryCode } } } |
            ConvertTo-Json -Depth 5 | Set-Content $file -Encoding utf8NoBOM
        try { Az rest --method PUT --uri $uri --headers Content-Type=application/json --body "@$file" | Out-Null } finally { Remove-Item $file }
    }
    $poll = if ($env:FACTORY_SETUP_POLL_SECONDS) { [int]$env:FACTORY_SETUP_POLL_SECONDS } else { 20 }
    $deadline = (Get-Date).AddMinutes(40)
    while (-not $t -or $t.properties.provisioningState -ne 'Succeeded' -or -not $t.properties.tenantId) {
        if ($t.properties.provisioningState -eq 'Failed') { throw "External tenant $subdomain failed to provision" }
        if ((Get-Date) -gt $deadline) { throw "External tenant $subdomain still provisioning after 40 minutes; re-run later" }
        Start-Sleep -Seconds $poll
        $t = & $get
    }
    $t.properties.tenantId
}

function Ensure-App([string] $name) {
    $app = @(AzJson ad app list --display-name $name --query '[].{appId:appId,id:id}')[0]
    if (-not $app) {
        Write-Host "  app registration ${name}: creating"
        $app = AzJson ad app create --display-name $name --query '{appId:appId,id:id}'
    }
    $app
}

function Ensure-Sp([string] $appId) {
    $id = Az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
    if (-not $id) { $id = Az ad sp create --id $appId --query id -o tsv }
    $id
}

function Ensure-Role([string] $spId, [string] $role, [string] $scope) {
    if (Az role assignment list --assignee $spId --role $role --scope $scope --query '[0].id' -o tsv) { return }
    Write-Host "  role ${role}: assigning"
    Az role assignment create --assignee-object-id $spId --assignee-principal-type ServicePrincipal --role $role --scope $scope | Out-Null
}

# HCP's token subject is organization:<org>:project:<p>:workspace:<w>:run_phase:<plan|apply>, and every
# preview gets a new workspace, so the credential matches a pattern (a flexible federated credential,
# Graph beta) rather than one exact subject.
function Ensure-HcpCredential([string] $appObjectId) {
    $uri = "/beta/applications/$appObjectId/federatedIdentityCredentials"
    if ((Graph GET $uri).value | Where-Object name -eq 'hcp-terraform') { return }
    Write-Host '  federated credential hcp-terraform: creating'
    Graph POST $uri @{
        name = 'hcp-terraform'; issuer = 'https://app.terraform.io'; audiences = @('api://AzureADTokenExchange')
        claimsMatchingExpression = @{ value = "claims['sub'] matches 'organization:${HcpOrg}:project:*:workspace:*:run_phase:*'"; languageVersion = 1 }
    } | Out-Null
}

# Application permissions on Microsoft Graph, granted directly (the same as admin consent).
function Ensure-GraphRoles([string] $spId, [string[]] $roles) {
    $graph = AzJson ad sp show --id $GraphAppId --query '{id:id,roles:appRoles[].{id:id,value:value}}'
    $have = @((Graph GET "/v1.0/servicePrincipals/$spId/appRoleAssignments").value | ForEach-Object appRoleId)
    foreach ($r in $roles) {
        $roleId = ($graph.roles | Where-Object value -eq $r).id
        if (-not $roleId) { throw "Microsoft Graph has no app role $r" }
        if ($roleId -in $have) { continue }
        Write-Host "  Graph role ${r}: granting"
        Graph POST "/v1.0/servicePrincipals/$spId/appRoleAssignments" @{ principalId = $spId; resourceId = $graph.id; appRoleId = $roleId } | Out-Null
    }
}

# Email + password sign-up and sign-in, collecting email and display name.
function Ensure-UserFlow([string] $name) {
    $flow = (Graph GET '/v1.0/identity/authenticationEventsFlows').value | Where-Object displayName -eq $name | Select-Object -First 1
    if ($flow) { return $flow.id }
    Write-Host "  user flow ${name}: creating"
    $attr = { param($id, $label) @{ id = $id; displayName = $label; description = $label; userFlowAttributeType = 'builtIn'; dataType = 'string' } }
    (Graph POST '/v1.0/identity/authenticationEventsFlows' @{
        '@odata.type' = '#microsoft.graph.externalUsersSelfServiceSignUpEventsFlow'
        displayName = $name
        onAuthenticationMethodLoadStart = @{
            '@odata.type' = '#microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp'
            identityProviders = @(@{ id = 'EmailPassword-OAUTH' })
        }
        onInteractiveAuthFlowStart = @{
            '@odata.type' = '#microsoft.graph.onInteractiveAuthFlowStartExternalUsersSelfServiceSignUp'
            isSignUpAllowed = $true
        }
        onAttributeCollection = @{
            '@odata.type' = '#microsoft.graph.onAttributeCollectionExternalUsersSelfServiceSignUp'
            attributes = @((& $attr 'email' 'Email Address'), (& $attr 'displayName' 'Display Name'))
            attributeCollectionPage = @{ views = @(@{ inputs = @(
                @{ attribute = 'email'; label = 'Email Address'; inputType = 'text'; hidden = $true; editable = $false; writeToDirectory = $true; required = $true }
                @{ attribute = 'displayName'; label = 'Display Name'; inputType = 'text'; hidden = $false; editable = $true; writeToDirectory = $true; required = $false }
            ) }) }
        }
    }).id
}

# NAME=value in the .env file: an active line first, else a commented-out one, else appended.
function Set-EnvValue([string] $name, [string] $value) {
    $lines = [Collections.Generic.List[string]]@(if (Test-Path $EnvFile) { Get-Content $EnvFile -Encoding utf8 })
    $i = $lines.FindIndex({ param($l) $l -match "^\s*$name\s*=" })
    if ($i -lt 0) { $i = $lines.FindIndex({ param($l) $l -match "^\s*#\s*$name\s*=" }) }
    if ($i -ge 0) { $lines[$i] = "$name=$value" } else { $lines.Add("$name=$value") }
    Set-Content $EnvFile $lines -Encoding utf8NoBOM
}

$original = Az account show --query id -o tsv
try {
    Write-Host "Subscription $SubscriptionId"
    Use-Account $SubscriptionId 'az login'
    $tenantId = Az account show --query tenantId -o tsv
    if (-not $tenantId) { throw "az returned no tenant id for subscription $SubscriptionId; run az login and try again" }
    $scope = "/subscriptions/$SubscriptionId"

    # Every namespace the templates' IaC deploys to. azurerm registers only its own core set, and a
    # new subscription lacks Microsoft.App: the Container Apps environment then fails with
    # MissingSubscriptionRegistration (canary-6). Registering is idempotent; --wait so the first
    # preview after setup does not race it.
    foreach ($ns in 'Microsoft.App', 'Microsoft.ContainerRegistry', 'Microsoft.KeyVault', 'Microsoft.DBforPostgreSQL',
                     'Microsoft.Sql', 'Microsoft.Storage', 'Microsoft.OperationalInsights', 'Microsoft.Insights',
                     'Microsoft.Web', 'Microsoft.ManagedIdentity', 'Microsoft.ServiceBus') {
        Az provider register --namespace $ns --subscription $SubscriptionId --wait | Out-Null
    }

    $gh = Ensure-App 'factory-github-actions'
    $ghSp = Ensure-Sp $gh.appId
    Ensure-Role $ghSp 'Contributor' $scope

    $hcp = Ensure-App 'factory-hcp-terraform'
    $hcpSp = Ensure-Sp $hcp.appId
    Ensure-Role $hcpSp 'Contributor' $scope
    Ensure-Role $hcpSp 'User Access Administrator' $scope
    Ensure-HcpCredential $hcp.id

    Set-EnvValue 'AZURE_GH_CLIENT_ID' $gh.appId
    Set-EnvValue 'AZURE_TENANT_ID' $tenantId
    Set-EnvValue 'AZURE_SUBSCRIPTION_ID' $SubscriptionId

    $varset = [ordered]@{
        TFC_AZURE_PROVIDER_AUTH = 'true'; TFC_AZURE_RUN_CLIENT_ID = $hcp.appId
        ARM_SUBSCRIPTION_ID = $SubscriptionId; ARM_TENANT_ID = $tenantId
        # A terraform variable: each environment's Key Vault grants this identity Secrets User, so the
        # workflow's schema step can read the connection string (its Contributor role is control-plane only).
        deployer_object_id = $ghSp
    }

    if ($CreateExternalTenant) {
        $ExternalTenantId = Ensure-ExternalTenant $CreateExternalTenant
        if (-not $ExternalSubdomain) { $ExternalSubdomain = $CreateExternalTenant }
    }

    if ($ExternalTenantId) {
        Write-Host "External tenant $ExternalTenantId"
        Use-Tenant $ExternalTenantId
        if (-not $ExternalSubdomain) {
            $initial = (Graph GET '/v1.0/organization').value[0].verifiedDomains | Where-Object isInitial | Select-Object -First 1
            $ExternalSubdomain = ($initial.name -split '\.')[0]
        }
        $flowId = Ensure-UserFlow $UserFlowName
        $ext = Ensure-App 'factory-hcp-terraform'
        $extSp = Ensure-Sp $ext.appId
        Ensure-GraphRoles $extSp $HcpGraphRoles
        Ensure-HcpCredential $ext.id

        $varset.TFC_AZURE_PROVIDER_AUTH_EXTERNAL = 'true'
        $varset.TFC_AZURE_RUN_CLIENT_ID_EXTERNAL = $ext.appId
        $varset.entra_external_tenant_id = $ExternalTenantId
        $varset.entra_external_subdomain = $ExternalSubdomain
        $varset.entra_external_user_flow_id = $flowId
    }
} finally {
    Az account set --subscription $original | Out-Null
}

Write-Host "`nWrote AZURE_GH_CLIENT_ID, AZURE_TENANT_ID and AZURE_SUBSCRIPTION_ID to $EnvFile."
Write-Host 'HCP variable set (docs/DEPLOY.md section 4; deployer_object_id and entra_* are terraform variables, the rest env):'
foreach ($k in $varset.Keys) { "$k=$($varset[$k])" }

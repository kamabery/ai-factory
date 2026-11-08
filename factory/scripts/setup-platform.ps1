# The shared platform j<NNN> previews run on; see docs/DEPLOY.md, "The shared platform". One Container
# Apps environment, Log Analytics workspace, Key Vault and Postgres server for the subscription,
# because those are what make a preview slow: about 17 of its 31 minutes to create and 25 of its 26 to
# destroy. About $18 a month, nearly all of it the Postgres server.
#
# This is az rather than Terraform on purpose. It belongs to the subscription and never to one
# scaffold, it is set up once, and nothing else needs its state. Everything is looked up before it is
# created, so re-running is safe, and the admin password is only ever generated with the server.
# Run setup-azure.ps1 first: it makes the identities this grants.
param(
    [string] $SubscriptionId,
    [string] $Location = 'centralus',   # as IaC/envs/ephemeral.tfvars: a preview elsewhere keeps its own stack
    [string] $ResourceGroup = 'rg-factory-platform',
    [string] $EnvFile = (Join-Path $PSScriptRoot '..\..\.env')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env-file.ps1')

$vars = Read-EnvFile $EnvFile
if (-not $SubscriptionId) { $SubscriptionId = $vars['AZURE_SUBSCRIPTION_ID'] }
if (-not $SubscriptionId) { throw 'Pass -SubscriptionId, or set AZURE_SUBSCRIPTION_ID in .env' }
if (-not $vars['HCP_TOKEN'] -or -not $vars['HCP_AZURE_VARSET']) { throw 'Set HCP_TOKEN and HCP_AZURE_VARSET in .env: the platform goes on that variable set' }

$AzExe = if (-not $env:FACTORY_AZ_CMD) { (Get-Command az -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
$script:Secret = $null
# Returns stdout trimmed; throws on failure, with the password masked: it is passed as an argument.
function Az {
    $all = if ($env:FACTORY_AZ_CMD) { & pwsh -NoProfile -File $env:FACTORY_AZ_CMD @args 2>&1 } else { & $AzExe @args 2>&1 }
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        $msg = "az $($args -join ' ') failed: $(($all | Out-String).Trim())"
        throw $(if ($script:Secret) { $msg.Replace($script:Secret, '***') } else { $msg })
    }
    (@($all | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }) -join "`n").Trim()
}

function Exists([string] $type, [string] $name) {
    [bool](Az resource list --resource-group $ResourceGroup --resource-type $type --name $name --query '[0].id' -o tsv)
}

function Ensure-Role([string] $principal, [string] $type, [string] $role, [string] $scope) {
    if (Az role assignment list --assignee $principal --role $role --scope $scope --query '[0].id' -o tsv) { return }
    Write-Host "  role $role for ${principal}: assigning"
    Az role assignment create --assignee-object-id $principal --assignee-principal-type $type --role $role --scope $scope | Out-Null
}

function Identity([string] $name) {
    $id = Az ad sp list --display-name $name --query '[0].id' -o tsv
    if (-not $id) { throw "no $name service principal: run setup-azure.ps1 first" }
    $id
}

# The HCP variable set's variable $key, created or changed only when it differs.
function Set-VarsetVariable([string] $key, [string] $value) {
    $api = "$(if ($vars['HCP_API_URL']) { $vars['HCP_API_URL'].TrimEnd('/') } else { 'https://app.terraform.io' })/api/v2/varsets/$($vars['HCP_AZURE_VARSET'])/relationships/vars"
    $h = @{ Authorization = "Bearer $($vars['HCP_TOKEN'])"; 'Content-Type' = 'application/vnd.api+json' }
    $have = @((Invoke-RestMethod -Uri $api -Headers $h).data) | Where-Object { $_.attributes.key -eq $key } | Select-Object -First 1
    if ($have -and $have.attributes.value -eq $value) { return }
    $body = @{ data = @{ type = 'vars'; attributes = @{ key = $key; value = $value; category = 'terraform'; hcl = $true; sensitive = $false } } } | ConvertTo-Json -Depth 5
    if ($have) { Invoke-RestMethod -Method Patch -Uri "$api/$($have.id)" -Headers $h -Body $body | Out-Null }
    else { Invoke-RestMethod -Method Post -Uri $api -Headers $h -Body $body | Out-Null }
    Write-Host "  HCP variable ${key}: set"
}

function New-Password {
    # Alphanumeric, so it needs no escaping on a command line or in a connection string.
    $chars = [char[]]'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    -join (1..40 | ForEach-Object { $chars[[Security.Cryptography.RandomNumberGenerator]::GetInt32($chars.Count)] })
}

# Vault and server names are global, so they carry a hash of the subscription: stable across runs.
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($SubscriptionId))).Substring(0, 8).ToLowerInvariant()
$LogName, $EnvName, $VaultName, $ServerName = 'log-factory-platform', 'cae-factory-platform', "kv-factory-$hash", "psql-factory-$hash"

$original = Az account show --query id -o tsv
try {
    Az account set --subscription $SubscriptionId | Out-Null
    $hcpSp = Identity 'factory-hcp-terraform'
    $ghSp = Identity 'factory-github-actions'

    Write-Host "Platform in $ResourceGroup, $Location"
    Az group create --name $ResourceGroup --location $Location --tags managed_by=factory | Out-Null

    if (-not (Exists 'Microsoft.OperationalInsights/workspaces' $LogName)) {
        Write-Host "  $($LogName): creating"
        # A daily cap near the free 5 GB a month, because one noisy preview now spends for all of them.
        Az monitor log-analytics workspace create --resource-group $ResourceGroup --workspace-name $LogName --location $Location --retention-time 30 --quota 0.2 | Out-Null
    }
    $logId = Az monitor log-analytics workspace show --resource-group $ResourceGroup --workspace-name $LogName --query id -o tsv

    if (-not (Exists 'Microsoft.App/managedEnvironments' $EnvName)) {
        Write-Host "  $($EnvName): creating (about 8 minutes)"
        $customer = Az monitor log-analytics workspace show --resource-group $ResourceGroup --workspace-name $LogName --query customerId -o tsv
        $key = Az monitor log-analytics workspace get-shared-keys --resource-group $ResourceGroup --workspace-name $LogName --query primarySharedKey -o tsv
        # Otherwise az stops to ask whether to install it.
        Az extension add --name containerapp --upgrade --yes | Out-Null
        Az containerapp env create --name $EnvName --resource-group $ResourceGroup --location $Location --logs-workspace-id $customer --logs-workspace-key $key | Out-Null
    }
    $envId = Az containerapp env show --name $EnvName --resource-group $ResourceGroup --query id -o tsv
    $domain = Az containerapp env show --name $EnvName --resource-group $ResourceGroup --query properties.defaultDomain -o tsv

    if (-not (Exists 'Microsoft.KeyVault/vaults' $VaultName)) {
        Write-Host "  $($VaultName): creating"
        Az keyvault create --name $VaultName --resource-group $ResourceGroup --location $Location --enable-rbac-authorization true --retention-days 7 | Out-Null
    }
    $vaultId = Az keyvault show --name $VaultName --resource-group $ResourceGroup --query id -o tsv
    # HCP's runs write each preview's secrets, and the workflow reads them. An app gets only its own,
    # from the preview's Terraform. Whoever runs this writes the admin password below.
    Ensure-Role $hcpSp 'ServicePrincipal' 'Key Vault Secrets Officer' $vaultId
    Ensure-Role $ghSp 'ServicePrincipal' 'Key Vault Secrets User' $vaultId

    if (-not (Exists 'Microsoft.DBforPostgreSQL/flexibleServers' $ServerName)) {
        Ensure-Role (Az ad signed-in-user show --query id -o tsv) 'User' 'Key Vault Secrets Officer' $vaultId
        $script:Secret = New-Password
        # The vault first: a server whose password was never stored could not be used by anything.
        $poll = if ($null -ne $env:FACTORY_SETUP_POLL_SECONDS) { [int]$env:FACTORY_SETUP_POLL_SECONDS } else { 15 }
        for ($i = 1; ; $i++) {
            try { Az keyvault secret set --vault-name $VaultName --name postgres-admin-password --value $script:Secret | Out-Null; break }
            catch { if ($i -ge 40 -or "$_" -notmatch 'Forbidden') { throw }; Start-Sleep -Seconds $poll }
        }
        Write-Host "  $($ServerName): creating (about 5 minutes)"
        Az postgres flexible-server create --name $ServerName --resource-group $ResourceGroup --location $Location --tier Burstable --sku-name Standard_B1ms `
            --storage-size 32 --version 16 --backup-retention 7 --admin-user pgadmin --admin-password $script:Secret --public-access 0.0.0.0 --yes | Out-Null
    }
    $serverId = Az postgres flexible-server show --name $ServerName --resource-group $ResourceGroup --query id -o tsv
    $fqdn = Az postgres flexible-server show --name $ServerName --resource-group $ResourceGroup --query fullyQualifiedDomainName -o tsv

    $platform = [ordered]@{
        location = $Location
        environment_id = $envId; environment_name = $EnvName; environment_domain = $domain
        log_analytics_workspace_id = $logId
        key_vault_id = $vaultId; key_vault_name = $VaultName
        postgres_server_id = $serverId; postgres_server_name = $ServerName; postgres_server_fqdn = $fqdn; postgres_resource_group = $ResourceGroup
    }
    $lines = $platform.Keys | ForEach-Object { '  {0} = "{1}"' -f $_, $platform[$_] }
    Set-VarsetVariable 'platform' (@('{') + $lines + @('}') -join "`n")
} finally {
    if ($original) { Az account set --subscription $original | Out-Null }
}

Write-Host "`nThe platform is up, and on the HCP variable set as the terraform variable platform."
Write-Host 'That changes the canary fingerprint, so the next deployable job runs a canary first.'
foreach ($k in $platform.Keys) { "$k=$($platform[$k])" }

locals {
  # Global names carry a hash of the subscription: stable across applies, unique per subscription.
  hash         = substr(sha256(var.subscription_id), 0, 8)
  vault_name   = "kv-fcloud-${local.hash}"
  storage_name = "stfcloud${local.hash}"
  sql_name     = "sql-factory-cloud-${local.hash}"
  job_name     = "job-factory-runner"
  # The runner job's id, known before it exists: the job starts itself again (runner --one), so it cannot wait on its own output.
  job_id = "/subscriptions/${var.subscription_id}/resourceGroups/${var.resource_group_name}/providers/Microsoft.App/jobs/${local.job_name}"
  tags   = { managed_by = "factory" }

  board_database = {
    name                        = "factory"
    sku_name                    = "GP_S_Gen5_1"
    min_capacity                = 0.5
    auto_pause_delay_in_minutes = 15
    max_size_gb                 = 2
    storage_account_type        = "Local"
    zone_redundant              = false
  }
}

data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "cloud" {
  name     = var.resource_group_name
  location = var.location
  tags     = local.tags
}

# The deployed factory's only credential to Azure.
resource "azurerm_user_assigned_identity" "factory" {
  name                = "id-factory-cloud"
  resource_group_name = azurerm_resource_group.cloud.name
  location            = azurerm_resource_group.cloud.location
  tags                = local.tags
}

# The secrets (GITHUB_APP_*, GITHUB_USER_*, HCP_*), copied in by `factory init cloud`. The identity writes back the
# refreshed GitHub user token, so it is Secrets Officer; whoever applies this is too, for init cloud. RBAC only, and no
# firewall: Container Apps on Consumption has no fixed outbound address, so access is Entra alone, as state's is.
module "vault" {
  source  = "Azure/avm-res-keyvault-vault/azurerm"
  version = "0.13.1"

  name                          = local.vault_name
  location                      = azurerm_resource_group.cloud.location
  parent_id                     = azurerm_resource_group.cloud.id
  tenant_id                     = var.tenant_id
  sku_name                      = "standard"
  soft_delete_retention_days    = 7
  purge_protection_enabled      = false
  public_network_access_enabled = true
  network_acls                  = null
  enable_telemetry              = false
  tags                          = local.tags

  role_assignments = {
    factory = { role_definition_id_or_name = "Key Vault Secrets Officer", principal_id = azurerm_user_assigned_identity.factory.principal_id, principal_type = "ServicePrincipal" }
    admin   = { role_definition_id_or_name = "Key Vault Secrets Officer", principal_id = data.azurerm_client_config.current.object_id }
  }
}

# The artifacts (specs, rounds, run logs, transcripts, sessions): FACTORY_BLOB_URL. No shared keys; the identity is
# Storage Blob Data Owner on the container only (prune deletes blob versions).
module "storage" {
  source  = "Azure/avm-res-storage-storageaccount/azurerm"
  version = "0.11.0"

  name                          = local.storage_name
  location                      = azurerm_resource_group.cloud.location
  parent_id                     = azurerm_resource_group.cloud.id
  account_replication_type      = "LRS"
  shared_access_key_enabled     = false
  public_network_access_enabled = true
  network_rules                 = null
  min_tls_version               = "TLS1_2"
  enable_telemetry              = false
  tags                          = local.tags

  containers = {
    factory = {
      name = "factory"
      role_assignments = {
        factory = { role_definition_id_or_name = "Storage Blob Data Owner", principal_id = azurerm_user_assigned_identity.factory.principal_id, principal_type = "ServicePrincipal" }
      }
    }
  }
}

# The board and the events (FACTORY_STATE=sql), Entra only. The identity is the server's Entra admin, so it needs no
# database user made by hand; nothing else signs in. Serverless, pausing after an idle hour.
module "sql" {
  source  = "Azure/avm-res-sql-server/azurerm"
  version = "0.2.1"

  name                          = local.sql_name
  location                      = azurerm_resource_group.cloud.location
  resource_group_name           = azurerm_resource_group.cloud.name
  server_version                = "12.0"
  public_network_access_enabled = true
  enable_telemetry              = false
  tags                          = local.tags

  azuread_administrator = {
    login_username              = azurerm_user_assigned_identity.factory.name
    object_id                   = azurerm_user_assigned_identity.factory.principal_id
    tenant_id                   = var.tenant_id
    azuread_authentication_only = true
  }

  # Azure's own services (the 0.0.0.0 rule): the containers' outbound addresses are not fixed.
  firewall_rules = {
    azure = { start_ip_address = "0.0.0.0", end_ip_address = "0.0.0.0" }
  }

  databases = {
    factory = local.board_database
  }
}

# Foundry User on the account, by id so a rename cannot break it, and nothing wider.
resource "azurerm_role_assignment" "foundry" {
  scope              = var.foundry_account_id
  role_definition_id = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d"
  principal_id       = azurerm_user_assigned_identity.factory.principal_id
  principal_type     = "ServicePrincipal"
}

# The identity starts its own runner's executions (approvals, the watchdog, runner --one), and no other job's.
resource "azurerm_role_assignment" "runner" {
  scope                = module.runner.resource_id
  role_definition_name = "Container Apps Jobs Operator"
  principal_id         = azurerm_user_assigned_identity.factory.principal_id
  principal_type       = "ServicePrincipal"
}

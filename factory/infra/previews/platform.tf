# The shared platform j<NNN> previews run on (docs/DEPLOY.md, "The shared platform"): one Container Apps environment,
# Log Analytics workspace, Key Vault and Postgres server for the subscription, because those are what make a preview
# slow (about 17 of its 31 minutes to create, 25 of its 26 to destroy). About $18 a month, nearly all of it Postgres.

locals {
  # Vault and server names are global, so they carry a hash of the subscription: stable across applies.
  platform_hash = substr(sha256(var.subscription_id), 0, 8)
  vault_name    = "kv-factory-${local.platform_hash}"
  server_name   = "psql-factory-${local.platform_hash}"
}

data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "platform" {
  name     = var.platform_resource_group
  location = var.location
  tags     = { managed_by = "factory" }
}

resource "azurerm_log_analytics_workspace" "platform" {
  name                = "log-factory-platform"
  resource_group_name = azurerm_resource_group.platform.name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  # Near the free 5 GB a month, because one noisy preview spends for all of them.
  daily_quota_gb = 0.2
}

resource "azurerm_container_app_environment" "platform" {
  name                       = "cae-factory-platform"
  resource_group_name        = azurerm_resource_group.platform.name
  location                   = var.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.platform.id
  logs_destination           = "log-analytics"

  # No fixed charge: previews scale to zero on the Consumption profile.
  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }
}

resource "azurerm_key_vault" "platform" {
  name                       = local.vault_name
  resource_group_name        = azurerm_resource_group.platform.name
  location                   = var.location
  tenant_id                  = var.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  soft_delete_retention_days = 7
}

# HCP's runs write each preview's secrets and the workflow reads them; an app gets only its own, from the preview's
# Terraform. Whoever applies this writes the Postgres password.
resource "azurerm_role_assignment" "vault" {
  for_each = {
    hcp   = { principal = azuread_service_principal.hcp.object_id, type = "ServicePrincipal", role = "Key Vault Secrets Officer" }
    gh    = { principal = azuread_service_principal.github.object_id, type = "ServicePrincipal", role = "Key Vault Secrets User" }
    admin = { principal = coalesce(var.keyvault_admin_object_id, data.azurerm_client_config.current.object_id), type = "User", role = "Key Vault Secrets Officer" }
  }

  scope                = azurerm_key_vault.platform.id
  role_definition_name = each.value.role
  principal_id         = each.value.principal
  principal_type       = each.value.type
}

# Alphanumeric, so it needs no escaping on a command line or in a connection string. An adopted server keeps the
# password it was made with: neither the secret nor the server follows a new one.
resource "random_password" "postgres" {
  length  = 40
  special = false
}

# The vault first: a server whose password was never stored could not be used by anything.
resource "azurerm_key_vault_secret" "postgres" {
  name         = "postgres-admin-password"
  key_vault_id = azurerm_key_vault.platform.id
  value        = random_password.postgres.result

  depends_on = [azurerm_role_assignment.vault["admin"]]

  lifecycle {
    ignore_changes = [value]
  }
}

resource "azurerm_postgresql_flexible_server" "platform" {
  name                          = local.server_name
  resource_group_name           = azurerm_resource_group.platform.name
  location                      = var.location
  version                       = "16"
  sku_name                      = "B_Standard_B1ms"
  storage_mb                    = 32768
  backup_retention_days         = 7
  administrator_login           = "pgadmin"
  administrator_password        = random_password.postgres.result
  public_network_access_enabled = true

  depends_on = [azurerm_key_vault_secret.postgres]

  lifecycle {
    # Azure picks the zone; the password is the vault's (above).
    ignore_changes = [zone, administrator_password]
  }
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "azure" {
  name             = var.postgres_azure_rule_name
  server_id        = azurerm_postgresql_flexible_server.platform.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

locals {
  # The HCP variable `platform` every preview's IaC reads (an HCL object, in this order).
  platform = {
    location                   = var.location
    environment_id             = azurerm_container_app_environment.platform.id
    environment_name           = azurerm_container_app_environment.platform.name
    environment_domain         = azurerm_container_app_environment.platform.default_domain
    log_analytics_workspace_id = azurerm_log_analytics_workspace.platform.id
    key_vault_id               = azurerm_key_vault.platform.id
    key_vault_name             = azurerm_key_vault.platform.name
    postgres_server_id         = azurerm_postgresql_flexible_server.platform.id
    postgres_server_name       = azurerm_postgresql_flexible_server.platform.name
    postgres_server_fqdn       = azurerm_postgresql_flexible_server.platform.fqdn
    postgres_resource_group    = azurerm_resource_group.platform.name
  }
  platform_keys = [
    "location", "environment_id", "environment_name", "environment_domain", "log_analytics_workspace_id", "key_vault_id",
    "key_vault_name", "postgres_server_id", "postgres_server_name", "postgres_server_fqdn", "postgres_resource_group",
  ]
  platform_hcl = join("\n", concat(["{"], [for k in local.platform_keys : "  ${k} = \"${local.platform[k]}\""], ["}"]))
}

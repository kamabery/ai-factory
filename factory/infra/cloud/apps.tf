# The dashboard and the runner: the same image, the same environment, the same identity (contract, "Cloud mode
# (deployed)"). Nothing reads a .env in a container: every setting is here, every secret in the vault.

locals {
  environment = merge(var.settings, {
    FACTORY_MODE             = "cloud"
    FACTORY_FOUNDRY_RESOURCE = var.foundry_resource_name
    FACTORY_STATE            = "sql"
    FACTORY_SQL              = "Server=tcp:${local.sql_name}.database.windows.net,1433;Database=factory;Authentication=Active Directory Default;Encrypt=True"
    FACTORY_BLOB_URL         = "https://${local.storage_name}.blob.core.windows.net/factory"
    FACTORY_SECRET_VAULT     = module.vault.uri
    AZURE_CLIENT_ID          = azurerm_user_assigned_identity.factory.client_id
    FACTORY_RUNNER_JOB       = local.job_id
    GITHUB_OWNER             = var.github_owner
  })
  env = [for name in sort(keys(local.environment)) : { name = name, value = local.environment[name] }]
}

module "dashboard" {
  source  = "Azure/avm-res-app-containerapp/azurerm"
  version = "0.9.0"

  name                                  = "ca-factory-dashboard"
  resource_group_name                   = azurerm_resource_group.cloud.name
  location                              = azurerm_resource_group.cloud.location
  container_app_environment_resource_id = var.container_app_environment_id
  workload_profile_name                 = "Consumption"
  revision_mode                         = "Single"
  enable_telemetry                      = false
  tags                                  = local.tags

  managed_identities = { user_assigned_resource_ids = [azurerm_user_assigned_identity.factory.id] }

  # One replica at most: serve.lock is a file in the container. Zero when no one has the dashboard open.
  template = {
    min_replicas = 0
    max_replicas = 1
    containers = [{
      name   = "factory"
      image  = var.image
      args   = ["serve"]
      cpu    = 0.5
      memory = "1Gi"
      env    = local.env
    }]
  }

  ingress = {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"
    traffic_weight   = [{ latest_revision = true, percentage = 100 }]
  }

  secrets = {
    fic = { name = local.fic_secret, value = azurerm_user_assigned_identity.factory.client_id }
  }

  auth_configs = {
    current = {
      name     = "current"
      platform = { enabled = true }
      global_validation = {
        unauthenticated_client_action = "RedirectToLoginPage"
        redirect_to_provider          = "azureactivedirectory"
      }
      identity_providers = {
        azure_active_directory = {
          enabled = true
          registration = {
            open_id_issuer             = "https://login.microsoftonline.com/${var.tenant_id}/v2.0"
            client_id                  = azuread_application.dashboard.client_id
            client_secret_setting_name = local.fic_secret
          }
        }
      }
    }
  }
}

# One execution per job; each is deleted when it exits. Four hours covers a canary and a deployable job's previews.
module "runner" {
  source  = "Azure/avm-res-app-job/azurerm"
  version = "0.2.2"

  name                                  = local.job_name
  resource_group_name                   = azurerm_resource_group.cloud.name
  location                              = azurerm_resource_group.cloud.location
  container_app_environment_resource_id = var.container_app_environment_id
  workload_profile_name                 = "Consumption"
  enable_telemetry                      = false
  tags                                  = local.tags

  managed_identities = { user_assigned_resource_ids = [azurerm_user_assigned_identity.factory.id] }

  trigger_config = {
    manual_trigger_config = { parallelism = 1, replica_completion_count = 1 }
  }
  replica_timeout_in_seconds = 14400
  replica_retry_limit        = 0

  template = {
    container = {
      name   = "factory"
      image  = var.image
      args   = ["job"]
      cpu    = 2
      memory = "4Gi"
      env    = local.env
    }
  }
}

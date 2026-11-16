# Offline: every provider is mocked, so this needs no Azure and no HCP (terraform init -backend=false; terraform test).
# It pins what the deployed factory relies on (contract, "Cloud mode (deployed)"): the identity as its only credential
# and the roles it holds, the containers' environment, Easy Auth's assignment-only, keyless sign-in, and the budget.

mock_provider "azurerm" {
  mock_resource "azurerm_resource_group" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-cloud" }
  }
  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-cloud/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-factory-cloud"
      principal_id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
      client_id    = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
    }
  }
  mock_data "azurerm_client_config" {
    defaults = { object_id = "99999999-9999-9999-9999-999999999999" }
  }
}

mock_provider "azapi" {}
mock_provider "modtm" {}
mock_provider "random" {}
mock_provider "time" {}

mock_provider "azuread" {
  mock_resource "azuread_application" {
    defaults = { id = "/applications/11111111-1111-1111-1111-111111111111", client_id = "22222222-2222-2222-2222-222222222222" }
  }
  mock_resource "azuread_service_principal" {
    defaults = { object_id = "33333333-3333-3333-3333-333333333333" }
  }
}

# The AVM modules are Microsoft's and tested there; here they stand in with their outputs, and the root's wiring is what
# is pinned.
override_module {
  target  = module.vault
  outputs = { uri = "https://kv-fcloud-test.vault.azure.net/", resource_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-cloud/providers/Microsoft.KeyVault/vaults/kv-fcloud-test" }
}

override_module {
  target  = module.storage
  outputs = { resource_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-cloud/providers/Microsoft.Storage/storageAccounts/stfcloudtest" }
}

override_module {
  target  = module.sql
  outputs = { resource_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-cloud/providers/Microsoft.Sql/servers/sql-factory-cloud-test" }
}

override_module {
  target  = module.dashboard
  outputs = { fqdn_url = "https://ca-factory-dashboard.test.centralus.azurecontainerapps.io", resource_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-cloud/providers/Microsoft.App/containerApps/ca-factory-dashboard" }
}

override_module {
  target  = module.runner
  outputs = { resource_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-cloud/providers/Microsoft.App/jobs/job-factory-runner" }
}

# Pinned here, so a machine's local.auto.tfvars never leaks into the test.
variables {
  subscription_id              = "00000000-0000-0000-0000-000000000000"
  tenant_id                    = "12121212-1212-1212-1212-121212121212"
  location                     = "centralus"
  resource_group_name          = "rg-factory-cloud"
  container_app_environment_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-platform/providers/Microsoft.App/managedEnvironments/cae-factory-platform"
  foundry_account_id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-llm/providers/Microsoft.CognitiveServices/accounts/fdy-factory-test"
  foundry_resource_name        = "fdy-factory-test"
  image                        = "ghcr.io/kamabery/ai-factory:0.3.0"
  github_owner                 = "kamabery"
  signin_user_object_ids       = ["cccccccc-cccc-cccc-cccc-cccccccccccc", "dddddddd-dddd-dddd-dddd-dddddddddddd"]
  settings                     = { HCP_ORG = "kurt-mabery", FACTORY_MODE = "local" }
  monthly_budget               = 25
  budget_start_date            = "2026-12-01T00:00:00Z"
  contact_email                = "factory@example.com"
}

run "the_identity_is_the_only_credential_with_foundry_user_and_its_own_runner_only" {
  command = apply

  assert {
    condition     = azurerm_role_assignment.foundry.scope == var.foundry_account_id && endswith(azurerm_role_assignment.foundry.role_definition_id, "/53ca6127-db72-4b80-b1b0-d745d6d5456d")
    error_message = "the identity must have Foundry User on the account, by id, and nothing wider"
  }
  assert {
    condition     = azurerm_role_assignment.runner.role_definition_name == "Container Apps Jobs Operator" && azurerm_role_assignment.runner.principal_id == azurerm_user_assigned_identity.factory.principal_id
    error_message = "the identity starts its own runner job's executions"
  }
  assert {
    condition     = output.runner_job_id == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-cloud/providers/Microsoft.App/jobs/job-factory-runner"
    error_message = "the runner job's id is known before the job exists"
  }
}

run "both_containers_run_cloud_mode_on_sql_with_shared_files_the_vault_and_no_key" {
  command = apply

  assert {
    condition = alltrue([
      local.environment["FACTORY_MODE"] == "cloud",
      local.environment["FACTORY_STATE"] == "sql",
      local.environment["FACTORY_FOUNDRY_RESOURCE"] == "fdy-factory-test",
      local.environment["AZURE_CLIENT_ID"] == "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
      local.environment["FACTORY_RUNNER_JOB"] == output.runner_job_id,
      local.environment["GITHUB_OWNER"] == "kamabery",
      local.environment["HCP_ORG"] == "kurt-mabery",
    ])
    error_message = "the containers' environment is the contract's, and settings never override the run mode"
  }
  assert {
    condition     = strcontains(local.environment["FACTORY_SQL"], "Authentication=Active Directory Default") && !strcontains(lower(local.environment["FACTORY_SQL"]), "password")
    error_message = "Azure SQL is Entra only: the connection string holds no password"
  }
  assert {
    condition     = startswith(local.environment["FACTORY_BLOB_URL"], "https://stfcloud") && endswith(local.environment["FACTORY_BLOB_URL"], ".blob.core.windows.net/factory")
    error_message = "the artifacts are shared through the factory container"
  }
  assert {
    condition     = length([for k in keys(local.environment) : k if strcontains(k, "ANTHROPIC") || strcontains(k, "KEY") || strcontains(k, "TOKEN")]) == 0
    error_message = "no key, token or Anthropic setting in a container's environment: the secrets are in the vault"
  }
  assert {
    condition     = length(local.env) == length(local.environment)
    error_message = "every setting is passed to the containers"
  }
}

run "easy_auth_lets_in_only_the_assigned_users_and_signs_in_keyless" {
  command = apply

  assert {
    condition     = azuread_service_principal.dashboard.app_role_assignment_required
    error_message = "assignment required: only the users named reach the dashboard"
  }
  assert {
    condition     = toset([for a in azuread_app_role_assignment.signin : a.principal_object_id]) == toset(var.signin_user_object_ids)
    error_message = "each user named is assigned, and no one else"
  }
  assert {
    condition = alltrue([
      azuread_application_federated_identity_credential.dashboard.subject == azurerm_user_assigned_identity.factory.principal_id,
      azuread_application_federated_identity_credential.dashboard.issuer == "https://login.microsoftonline.com/12121212-1212-1212-1212-121212121212/v2.0",
      azuread_application_federated_identity_credential.dashboard.audiences == tolist(["api://AzureADTokenExchange"]),
    ])
    error_message = "Easy Auth signs in as the managed identity through a federated credential, so the app has no secret"
  }
  assert {
    condition     = one(azuread_application_redirect_uris.dashboard.redirect_uris) == "https://ca-factory-dashboard.test.centralus.azurecontainerapps.io/.auth/login/aad/callback"
    error_message = "the app's callback is Easy Auth's, on the dashboard's address"
  }
  assert {
    condition     = output.cloud == { vault_uri = "https://kv-fcloud-test.vault.azure.net/", dashboard_url = "https://ca-factory-dashboard.test.centralus.azurecontainerapps.io" }
    error_message = "init cloud reads the vault and the dashboard from the cloud output"
  }
}

run "the_resource_group_has_its_budget" {
  command = apply

  assert {
    condition     = azurerm_consumption_budget_resource_group.cloud.amount == 25 && azurerm_consumption_budget_resource_group.cloud.time_grain == "Monthly"
    error_message = "a monthly budget of the amount given"
  }
}

run "no_one_to_sign_in_is_refused" {
  command = plan

  variables {
    signin_user_object_ids = []
  }

  expect_failures = [var.signin_user_object_ids]
}

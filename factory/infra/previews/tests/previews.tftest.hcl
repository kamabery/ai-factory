# Offline: every provider is mocked, so this needs no Azure and no HCP (terraform init -backend=false; terraform test).
# It pins what previews rely on: the identities' roles and their federated credential, the External tenant's Graph
# grants, the shared platform's names and settings, the variable set every preview reads, and what previews-env gets.

mock_provider "azurerm" {
  mock_resource "azurerm_resource_group" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-platform" }
  }
  mock_resource "azurerm_key_vault" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-platform/providers/Microsoft.KeyVault/vaults/kv-factory-test" }
  }
  mock_resource "azurerm_log_analytics_workspace" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-platform/providers/Microsoft.OperationalInsights/workspaces/log-factory-platform" }
  }
  mock_resource "azurerm_postgresql_flexible_server" {
    defaults = {
      id   = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-platform/providers/Microsoft.DBforPostgreSQL/flexibleServers/psql-factory-test"
      fqdn = "psql-factory-test.postgres.database.azure.com"
    }
  }
  mock_resource "azurerm_container_app_environment" {
    defaults = {
      id             = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-platform/providers/Microsoft.App/managedEnvironments/cae-factory-platform"
      default_domain = "test.centralus.azurecontainerapps.io"
    }
  }
  mock_data "azurerm_client_config" {
    defaults = { object_id = "99999999-9999-9999-9999-999999999999" }
  }
}

mock_provider "azuread" {
  mock_resource "azuread_application" {
    defaults = { id = "/applications/11111111-1111-1111-1111-111111111111", client_id = "22222222-2222-2222-2222-222222222222" }
  }
  mock_resource "azuread_service_principal" {
    defaults = { object_id = "33333333-3333-3333-3333-333333333333" }
  }
}

mock_provider "azuread" {
  alias = "external"

  mock_resource "azuread_application" {
    defaults = { id = "/applications/44444444-4444-4444-4444-444444444444", client_id = "55555555-5555-5555-5555-555555555555" }
  }
  mock_resource "azuread_service_principal" {
    defaults = { object_id = "66666666-6666-6666-6666-666666666666" }
  }
  mock_data "azuread_domains" {
    defaults = { domains = [{ domain_name = "factorypreviews.onmicrosoft.com" }] }
  }
  mock_data "azuread_service_principal" {
    defaults = {
      object_id = "77777777-7777-7777-7777-777777777777"
      app_role_ids = {
        "Application.ReadWrite.All"              = "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9"
        "User.ReadWrite.All"                     = "741f803b-c850-494e-b5df-cde7c675a1ca"
        "EventListener.ReadWrite.All"            = "0edf5e9e-4ce8-468a-8432-d08631d18c43"
        "DelegatedPermissionGrant.ReadWrite.All" = "8e8e4742-1d95-4f68-9d56-6ee75648c72a"
      }
    }
  }
}

mock_provider "msgraph" {
  alias = "external"

  mock_resource "msgraph_resource" {
    defaults = { id = "88888888-8888-8888-8888-888888888888" }
  }
}

mock_provider "tfe" {
  mock_resource "tfe_variable_set" {
    defaults = { id = "varset-AbCdEfGh12345678" }
  }
}

mock_provider "random" {}

variables {
  subscription_id = "00000000-0000-0000-0000-000000000000"
  tenant_id       = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
  hcp_org         = "test-org"
  contact_email   = "someone@example.com"
}

run "identities_and_their_roles" {
  command = apply

  assert {
    condition     = azurerm_role_assignment.github_contributor.role_definition_name == "Contributor" && azurerm_role_assignment.github_contributor.scope == "/subscriptions/00000000-0000-0000-0000-000000000000"
    error_message = "GitHub Actions needs Contributor on the subscription: each preview's resource group does not exist until it deploys"
  }
  assert {
    condition     = toset(keys(azurerm_role_assignment.hcp)) == toset(["Contributor", "User Access Administrator"]) && alltrue([for r in azurerm_role_assignment.hcp : r.scope == "/subscriptions/00000000-0000-0000-0000-000000000000"])
    error_message = "HCP needs Contributor and User Access Administrator on the subscription (it grants roles inside each preview)"
  }
  assert {
    condition     = azuread_application_flexible_federated_identity_credential.hcp.claims_matching_expression == "claims['sub'] matches 'organization:test-org:project:*:workspace:*:run_phase:*'"
    error_message = "HCP's credential must trust every workspace of this organization only (a flexible credential)"
  }
  assert {
    condition     = azuread_application_flexible_federated_identity_credential.hcp.issuer == "https://app.terraform.io" && azuread_application_flexible_federated_identity_credential.hcp.audience == "api://AzureADTokenExchange"
    error_message = "HCP's run tokens come from app.terraform.io for the Azure token exchange"
  }
}

run "no_external_tenant_makes_no_external_objects" {
  command = plan

  assert {
    condition     = length(msgraph_resource.user_flow) == 0 && length(azuread_application.external_hcp) == 0 && length(azuread_app_role_assignment.external_graph) == 0
    error_message = "without external_tenant_id there is no auth add-on, and nothing goes in an External tenant"
  }
  assert {
    condition     = !contains(keys(local.varset_env), "TFC_AZURE_RUN_CLIENT_ID_EXTERNAL")
    error_message = "the external credentials are on the variable set only with an External tenant"
  }
}

run "the_external_tenant" {
  command = apply

  variables {
    external_tenant_id = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
  }

  assert {
    condition = toset([for a in azuread_app_role_assignment.external_graph : a.app_role_id]) == toset([
      "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9", "741f803b-c850-494e-b5df-cde7c675a1ca", "0edf5e9e-4ce8-468a-8432-d08631d18c43", "8e8e4742-1d95-4f68-9d56-6ee75648c72a",
    ])
    error_message = "the External tenant's HCP app needs Application, User, EventListener and DelegatedPermissionGrant ReadWrite.All (J-022)"
  }
  assert {
    condition     = msgraph_resource.user_flow[0].body.displayName == "preview-signin" && msgraph_resource.user_flow[0].body.onInteractiveAuthFlowStart.isSignUpAllowed
    error_message = "the user flow is preview-signin, with sign-up allowed (previews create QA users)"
  }
  assert {
    condition     = azuread_application_flexible_federated_identity_credential.external_hcp[0].claims_matching_expression == local.hcp_claims
    error_message = "the External tenant's HCP app trusts the same organization pattern"
  }
  assert {
    condition     = local.varset_env["TFC_AZURE_PROVIDER_AUTH_EXTERNAL"] == "true" && local.varset_env["TFC_AZURE_RUN_CLIENT_ID_EXTERNAL"] == "55555555-5555-5555-5555-555555555555"
    error_message = "previews sign in to the External tenant through the variable set's _EXTERNAL credentials"
  }
  assert {
    condition = output.dotenv == {
      AZURE_GH_CLIENT_ID         = "22222222-2222-2222-2222-222222222222"
      AZURE_TENANT_ID            = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
      AZURE_SUBSCRIPTION_ID      = "00000000-0000-0000-0000-000000000000"
      HCP_AZURE_VARSET           = "varset-AbCdEfGh12345678"
      FACTORY_ENTRA_TENANT_ID    = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
      FACTORY_ENTRA_SUBDOMAIN    = "factorypreviews"
      FACTORY_ENTRA_USER_FLOW_ID = "88888888-8888-8888-8888-888888888888"
    }
    error_message = "previews-env gets exactly the contract's settings, and never HCP_ORG"
  }
}

run "the_shared_platform" {
  command = apply

  assert {
    condition     = azurerm_key_vault.platform.name == "kv-factory-${substr(sha256("00000000-0000-0000-0000-000000000000"), 0, 8)}" && azurerm_postgresql_flexible_server.platform.name == "psql-factory-${substr(sha256("00000000-0000-0000-0000-000000000000"), 0, 8)}"
    error_message = "vault and server names are global: they carry a hash of the subscription, stable across applies"
  }
  assert {
    condition     = azurerm_log_analytics_workspace.platform.daily_quota_gb == 0.2
    error_message = "Log Analytics stops at 0.2 GB a day, near the free 5 GB a month: one noisy preview spends for all"
  }
  assert {
    condition     = azurerm_postgresql_flexible_server.platform.sku_name == "B_Standard_B1ms" && azurerm_postgresql_flexible_server.platform.storage_mb == 32768
    error_message = "Postgres is the B1ms with 32 GB, the ~$18/month the docs quote"
  }
  assert {
    condition     = azurerm_role_assignment.vault["hcp"].role_definition_name == "Key Vault Secrets Officer" && azurerm_role_assignment.vault["gh"].role_definition_name == "Key Vault Secrets User"
    error_message = "HCP's runs write each preview's secrets and the workflow only reads them"
  }
  assert {
    condition     = azurerm_role_assignment.vault["admin"].principal_id == "99999999-9999-9999-9999-999999999999"
    error_message = "whoever applies gets Secrets Officer, to write the Postgres password"
  }
  assert {
    condition     = random_password.postgres.length == 40 && !random_password.postgres.special && nonsensitive(azurerm_key_vault_secret.postgres.value == azurerm_postgresql_flexible_server.platform.administrator_password)
    error_message = "the admin password is 40 alphanumerics, stored in the vault as the server's"
  }
  assert {
    condition     = startswith(tfe_variable.platform.value, "{\n  location = \"centralus\"\n  environment_id = \"/subscriptions/") && endswith(tfe_variable.platform.value, "\n  postgres_resource_group = \"rg-factory-platform\"\n}") && tfe_variable.platform.hcl
    error_message = "platform is an HCL object variable, in the order previews' IaC and the canary fingerprint have always seen"
  }
  assert {
    condition     = tfe_variable.deployer_object_id.value == "33333333-3333-3333-3333-333333333333" && tfe_variable.deployer_object_id.category == "terraform"
    error_message = "deployer_object_id is the GitHub Actions service principal, as a terraform variable"
  }
}

run "the_budget" {
  command = plan

  assert {
    condition     = azurerm_consumption_budget_subscription.previews.amount == 50 && length(azurerm_consumption_budget_subscription.previews.notification) == 4
    error_message = "a $50 monthly budget, emailing at 50/80/100% actual and 100% forecast"
  }
}

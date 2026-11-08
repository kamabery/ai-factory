# Offline: every provider is mocked, so this needs no Azure and no HCP (terraform init -backend=false).
# It pins what cloud mode relies on: keyless auth, the deployment names the factory's model pins
# default to, Azure-hosted pinned versions, and Foundry User on the account only.

mock_provider "azurerm" {
  # The module validates parent_id as a resource group id.
  mock_resource "azurerm_resource_group" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-llm" }
  }
}
mock_provider "azapi" {
  # Deployments and role assignments are children of the account, so its id must parse.
  mock_resource "azapi_resource" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-factory-llm/providers/Microsoft.CognitiveServices/accounts/fdy-factory-test" }
  }
}
mock_provider "random" {}

variables {
  subscription_id            = "00000000-0000-0000-0000-000000000000"
  principal_ids              = ["11111111-1111-1111-1111-111111111111"]
  provider_organization_name = "Test Org"
}

run "keyless_account" {
  # apply, against the mocks: the name has a random suffix, unknown at plan.
  command = apply

  # The module reports an AIServices account in AzureRM's schema (local_authentication_enabled);
  # other kinds report local_auth_enabled instead.
  assert {
    condition     = tostring(module.foundry_account.resource.local_authentication_enabled) == "false"
    error_message = "the account must be an AIServices account, and keyless: local (API key) auth disabled"
  }
  assert {
    condition     = module.foundry_account.resource.custom_subdomain_name == module.foundry_account.resource.name
    error_message = "Entra auth needs the custom subdomain, and FACTORY_FOUNDRY_RESOURCE is the account name, so they must match"
  }
}

run "claude_deployments" {
  command = plan

  assert {
    condition     = toset(keys(local.deployments)) == toset(["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"])
    error_message = "deployments must be named after their model ids, the names cloud mode pins by default"
  }
  assert {
    condition     = alltrue([for k, d in local.deployments : d.model.name == k && d.model.format == "Anthropic"])
    error_message = "each deployment name must equal its Claude model id"
  }
  assert {
    condition     = alltrue([for d in local.deployments : d.model.version == "2" && d.version_upgrade_option == "NoAutoUpgrade"])
    error_message = "versions are pinned to 2 (hosted on Azure) and never auto-upgraded"
  }
  assert {
    condition     = alltrue([for d in local.deployments : d.scale.type == "GlobalStandard"])
    error_message = "deployments are Global Standard"
  }
  # Azure refuses an Anthropic deployment without the Marketplace attestation (InvalidModelProviderData).
  assert {
    condition = alltrue([for k, d in azapi_resource.deployment : d.body.properties.modelProviderData == {
      organizationName = "Test Org", countryCode = "US", industry = "technology"
    }])
    error_message = "every Claude deployment must send modelProviderData: organizationName, countryCode, industry"
  }
  assert {
    condition     = alltrue([for k, d in azapi_resource.deployment : d.name == k && d.body.properties.model.name == k])
    error_message = "the deployment resources must be the ones in local.deployments"
  }
}

run "industry_must_be_listed" {
  command = plan
  variables {
    provider_industry = "Technology"
  }
  expect_failures = [var.provider_industry]
}

run "least_privilege_role" {
  command = plan

  assert {
    condition     = length(local.role_assignments) == 1
    error_message = "one role assignment per principal"
  }
  assert {
    condition     = alltrue([for r in local.role_assignments : endswith(r.role_definition_id_or_name, "/53ca6127-db72-4b80-b1b0-d745d6d5456d")])
    error_message = "principals get Foundry User (formerly Azure AI User), nothing wider"
  }
  # Azure stores the subscription-scoped id; an unscoped one replaces the assignment on every apply.
  assert {
    condition     = alltrue([for r in local.role_assignments : startswith(r.role_definition_id_or_name, "/subscriptions/${var.subscription_id}/providers/")])
    error_message = "the role definition id must be subscription-scoped, as Azure returns it, or every plan replaces the assignment"
  }
}

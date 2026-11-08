# The LLM cloud mode runs on: Claude in Microsoft Foundry (see "Run mode and providers" in
# docs/FACTORY-CONTRACT.md, and docs/FOUNDRY.md for the runbook).
#
# Keyless: local (API key) auth is off, so Entra ID is the only way in, and the identities that run
# the factory (you, later a managed identity) get Foundry User on this account and nothing wider.
# Deployments are named after their model ids, which is what the factory pins Claude Code's aliases
# to by default, and is how Claude Code detects each model's context window. Versions are pinned.
#
# State in HCP Terraform (workspace factory-llm, the factory's OIDC variable set attached), as
# factory/infra/state does. Pay per token: an idle account costs nothing.

terraform {
  required_version = ">= 1.9.0"

  # Organization from TF_CLOUD_ORGANIZATION (your HCP_ORG).
  cloud {
    workspaces {
      name = "factory-llm"
    }
  }

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.17"
    }
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.5"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

# The variable set carries two OIDC configurations, so HCP leaves the providers to opt in themselves
# (see factory/infra/state). Outside HCP: your own `az login`.
provider "azurerm" {
  features {}
  subscription_id      = var.subscription_id
  use_oidc             = local.hcp_credentials != null
  use_cli              = local.hcp_credentials == null
  client_id_file_path  = try(local.hcp_credentials.client_id_file_path, null)
  oidc_token_file_path = try(local.hcp_credentials.oidc_token_file_path, null)
}

provider "azapi" {
  subscription_id      = var.subscription_id
  use_oidc             = local.hcp_credentials != null
  use_cli              = local.hcp_credentials == null
  client_id_file_path  = try(local.hcp_credentials.client_id_file_path, null)
  oidc_token_file_path = try(local.hcp_credentials.oidc_token_file_path, null)
}

resource "random_string" "suffix" {
  length  = 6
  special = false
  upper   = false
}

resource "azurerm_resource_group" "llm" {
  name     = var.resource_group_name
  location = var.location
  tags     = local.tags
}

module "foundry_account" {
  source  = "Azure/avm-res-cognitiveservices-account/azurerm"
  version = "0.11.1"

  name      = local.name
  parent_id = azurerm_resource_group.llm.id
  location  = azurerm_resource_group.llm.location
  kind      = "AIServices"
  sku_name  = "S0"

  # Entra ID needs the custom subdomain; it is the account name, which is FACTORY_FOUNDRY_RESOURCE.
  custom_subdomain_name         = local.name
  local_auth_enabled            = false
  public_network_access_enabled = true

  role_assignments = local.role_assignments

  enable_telemetry = false
  tags             = local.tags
}

# Not the module's cognitive_deployments: its deployment submodule (0.11.1) cannot send
# modelProviderData, and Azure refuses an Anthropic deployment without it (InvalidModelProviderData).
# The block is the attestation the resource provider accepts the Azure Marketplace offer with.
resource "azapi_resource" "deployment" {
  for_each = local.deployments

  name      = each.key
  parent_id = module.foundry_account.resource_id
  type      = "Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview"
  body = {
    sku = { name = each.value.scale.type, capacity = each.value.scale.capacity }
    properties = {
      model                = each.value.model
      versionUpgradeOption = each.value.version_upgrade_option
      modelProviderData = {
        organizationName = var.provider_organization_name
        countryCode      = var.provider_country_code
        industry         = var.provider_industry
      }
    }
  }
  # One deployment at a time on the account, or Azure answers 409 Conflict.
  locks                     = [module.foundry_account.resource_id]
  schema_validation_enabled = false
}

locals {
  name            = "fdy-factory-${random_string.suffix.result}"
  tags            = { purpose = "ai-factory-llm" }
  hcp_credentials = try(var.tfc_azure_dynamic_credentials.default, null)

  deployments = {
    for model, capacity in var.deployments : model => {
      name                   = model
      version_upgrade_option = "NoAutoUpgrade"
      model                  = { format = "Anthropic", name = model, version = var.model_version }
      scale                  = { type = "GlobalStandard", capacity = capacity }
    }
  }

  # Foundry User (formerly Azure AI User), by id so a rename cannot break it.
  role_assignments = {
    for id in var.principal_ids : id => {
      role_definition_id_or_name = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d"
      principal_id               = id
    }
  }
}

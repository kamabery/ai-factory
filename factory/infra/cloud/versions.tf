# The deployed factory (docs/CLOUD.md; contract, "Cloud mode (deployed)"): one managed identity, Key Vault, the artifacts'
# storage, Azure SQL, the dashboard as a Container App behind Easy Auth, and the runner as a Container Apps job, on the
# preview platform's environment and the Foundry account factory/infra/llm made. Applied by hand, never by `factory`;
# then `factory init cloud` copies this machine's secrets into the vault.
#
# State in HCP Terraform, workspace factory-cloud, execution mode local: it runs here, under your `az login` and
# `terraform login`, as factory/infra/previews does. Organization from TF_CLOUD_ORGANIZATION.

terraform {
  required_version = ">= 1.9.0"

  cloud {
    workspaces {
      name = "factory-cloud"
    }
  }

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.81"
    }
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.12"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10"
    }
    modtm = {
      source  = "Azure/modtm"
      version = "~> 0.3"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.9"
    }
  }
}

provider "azurerm" {
  features {}
  subscription_id                 = var.subscription_id
  resource_provider_registrations = "none"
}

provider "azapi" {
  subscription_id = var.subscription_id
}

provider "azuread" {
  tenant_id = var.tenant_id
}

# The one-time setup ephemeral previews run on (docs/DEPLOY.md; contract, "Deploy"): the identities GitHub Actions and
# HCP Terraform sign in as, their roles and federated credentials, the Entra External ID tenant's user flow and app,
# the shared platform, the HCP variable set every preview reads, and the budget. Applied by hand, never by `factory`.
#
# State in HCP Terraform, workspace factory-setup, execution mode local: it runs here, under your `az login` (a work
# account such as factory-admin, never a personal one) and `terraform login`. Organization from TF_CLOUD_ORGANIZATION.

terraform {
  required_version = ">= 1.9.0"

  cloud {
    workspaces {
      name = "factory-setup"
    }
  }

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.17"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10"
    }
    msgraph = {
      source  = "microsoft/msgraph"
      version = "~> 0.6"
    }
    tfe = {
      source  = "hashicorp/tfe"
      version = "~> 0.84"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {}
  subscription_id = var.subscription_id

  # Every namespace the templates' IaC deploys to, registered when missing: azurerm's own core set lacks Microsoft.App,
  # and a new subscription then fails the Container Apps environment with MissingSubscriptionRegistration (canary-6).
  resource_provider_registrations = "none"
  resource_providers_to_register = [
    "Microsoft.App", "Microsoft.ContainerRegistry", "Microsoft.KeyVault", "Microsoft.DBforPostgreSQL", "Microsoft.Sql",
    "Microsoft.Storage", "Microsoft.OperationalInsights", "Microsoft.Insights", "Microsoft.Web",
    "Microsoft.ManagedIdentity", "Microsoft.ServiceBus", "Microsoft.Consumption",
  ]
}

provider "azuread" {
  tenant_id = var.tenant_id
}

# The External tenant, signed in to as the same account (az login --tenant <id> --allow-no-subscriptions). Graph is
# called with a token for that tenant, never through az rest.
provider "azuread" {
  alias     = "external"
  tenant_id = coalesce(var.external_tenant_id, var.tenant_id)
}

provider "msgraph" {
  alias     = "external"
  tenant_id = coalesce(var.external_tenant_id, var.tenant_id)
  use_cli   = true
}

provider "tfe" {
  organization = var.hcp_org
}

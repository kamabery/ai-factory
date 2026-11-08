# The factory's state store: one Blob container that FACTORY_STATE=azure makes the system of record
# (see "State backends" in docs/FACTORY-CONTRACT.md, and docs/STATE.md for the runbook).
#
# Secured by identity, not network spend: shared keys and SAS off, Entra RBAC only, TLS 1.2, and a
# deny-by-default firewall that `factory.ps1 allow-ip` opens to the machines that run the factory.
# No private endpoint (about $7.30/month each); a deployed factory reaches it through a VNet
# service endpoint, which is free.
#
# State in HCP Terraform (workspace factory-state, the factory's OIDC variable set attached), so
# this module's own state is not stored in the account it creates. HCP's runners are outside the
# firewall, so nothing here touches the blob data plane.

terraform {
  required_version = ">= 1.9.0"

  # Organization from TF_CLOUD_ORGANIZATION (your HCP_ORG).
  cloud {
    workspaces {
      name = "factory-state"
    }
  }

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.14"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {
    storage {
      # Management plane only: HCP's runners cannot reach the data plane through the firewall.
      data_plane_available = false
    }
  }
  subscription_id     = var.subscription_id
  storage_use_azuread = true

  # The variable set carries two OIDC configurations, so HCP leaves the provider to opt in itself
  # (see the template's providers.tf). Outside HCP: your own `az login`.
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

resource "azurerm_resource_group" "state" {
  name     = var.resource_group_name
  location = var.location
  tags     = local.tags
}

resource "azurerm_storage_account" "state" {
  name                     = "stfactory${random_string.suffix.result}"
  resource_group_name      = azurerm_resource_group.state.name
  location                 = azurerm_resource_group.state.location
  account_kind             = "StorageV2"
  account_tier             = "Standard"
  account_replication_type = "LRS"
  access_tier              = "Hot"

  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false
  public_network_access_enabled   = true

  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
    ip_rules       = var.ip_rules
  }

  blob_properties {
    # Every board.json write is recoverable; the factory's cap (FACTORY_STATE_CAP) prunes versions.
    versioning_enabled = true
  }

  tags = local.tags

  lifecycle {
    # `factory.ps1 allow-ip` owns the allowed IPs once the account exists.
    ignore_changes = [network_rules[0].ip_rules]
  }
}

resource "azurerm_storage_container" "factory" {
  name                  = "factory"
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"
}

# Owner, not Contributor: only Owner has deleteBlobVersion/action, which prune needs to drop old
# versions (Contributor gets 403 OperationNotAllowedOnAutomaticSnapshot).
resource "azurerm_role_assignment" "blob_data" {
  for_each             = toset(var.principal_ids)
  scope                = azurerm_storage_container.factory.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = each.value
}

# allow-ip edits the firewall; it needs the account's management plane, nothing more.
resource "azurerm_role_assignment" "firewall" {
  for_each             = toset(var.principal_ids)
  scope                = azurerm_storage_account.state.id
  role_definition_name = "Storage Account Contributor"
  principal_id         = each.value
}

locals {
  tags            = { purpose = "ai-factory-state" }
  hcp_credentials = try(var.tfc_azure_dynamic_credentials.default, null)
}

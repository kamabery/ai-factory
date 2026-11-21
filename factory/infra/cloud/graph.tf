# Deploys keep a federated credential per target on the previews' GitHub deploy app (contract, "Deploy"), which a
# workstation does under the human's az login. Here the identity does it: it owns that one app, and Graph's
# Application.ReadWrite.OwnedBy lets it change only apps it owns. Without previews (no AZURE_GH_CLIENT_ID), neither.

locals {
  deploy_app = lookup(var.settings, "AZURE_GH_CLIENT_ID", null)
}

data "azuread_application_published_app_ids" "well_known" {
  count = local.deploy_app == null ? 0 : 1
}

data "azuread_service_principal" "graph" {
  count     = local.deploy_app == null ? 0 : 1
  client_id = data.azuread_application_published_app_ids.well_known[0].result["MicrosoftGraph"]
}

data "azuread_application" "github" {
  count     = local.deploy_app == null ? 0 : 1
  client_id = local.deploy_app
}

resource "azuread_application_owner" "github" {
  count           = local.deploy_app == null ? 0 : 1
  application_id  = data.azuread_application.github[0].id
  owner_object_id = azurerm_user_assigned_identity.factory.principal_id
}

resource "azuread_app_role_assignment" "graph" {
  count               = local.deploy_app == null ? 0 : 1
  app_role_id         = data.azuread_service_principal.graph[0].app_role_ids["Application.ReadWrite.OwnedBy"]
  principal_object_id = azurerm_user_assigned_identity.factory.principal_id
  resource_object_id  = data.azuread_service_principal.graph[0].object_id
}

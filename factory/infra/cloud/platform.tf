# The deployed factory wakes the preview platform's Postgres server before a canary or a deploy, and its dashboard's
# Sleep stops it (contract, "The platform", spec 24). No built-in role is that narrow, so a custom one: read, start and
# stop, assignable to that one server only. Without previews (no FACTORY_PLATFORM_SERVER_ID in settings), neither.

locals {
  platform_server = lookup(var.settings, "FACTORY_PLATFORM_SERVER_ID", null)
}

resource "azurerm_role_definition" "platform" {
  count       = local.platform_server == null ? 0 : 1
  name        = "factory platform operator (${local.hash})"
  scope       = "/subscriptions/${var.subscription_id}"
  description = "Reads, starts and stops the preview platform's Postgres server, and nothing else."

  permissions {
    actions = [
      "Microsoft.DBforPostgreSQL/flexibleServers/read",
      "Microsoft.DBforPostgreSQL/flexibleServers/start/action",
      "Microsoft.DBforPostgreSQL/flexibleServers/stop/action",
    ]
    not_actions = []
  }

  assignable_scopes = [local.platform_server]
}

resource "azurerm_role_assignment" "platform" {
  count              = local.platform_server == null ? 0 : 1
  scope              = local.platform_server
  role_definition_id = azurerm_role_definition.platform[0].role_definition_resource_id
  principal_id       = azurerm_user_assigned_identity.factory.principal_id
  principal_type     = "ServicePrincipal"
}

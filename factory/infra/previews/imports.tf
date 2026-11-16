# Adopting what an earlier setup made (setup-azure.ps1 and setup-platform.ps1, or the portal): each block imports one
# object when var.adopt names it, so a plan shows "to import" rather than "to add", and Azure is not touched. A fresh
# install leaves var.adopt empty. The ids are the providers' import ids (docs/DEPLOY.md, "Adopting an existing setup").
# Once imported, a block is a no-op; leave it.
import {
  for_each = { for k, v in var.adopt : k => v if k == "github_app" }
  to       = azuread_application.github
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "github_sp" }
  to       = azuread_service_principal.github
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "github_contributor" }
  to       = azurerm_role_assignment.github_contributor
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "hcp_app" }
  to       = azuread_application.hcp
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "hcp_sp" }
  to       = azuread_service_principal.hcp
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "hcp_contributor" }
  to       = azurerm_role_assignment.hcp["Contributor"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "hcp_uaa" }
  to       = azurerm_role_assignment.hcp["User Access Administrator"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "hcp_fic" }
  to       = azuread_application_flexible_federated_identity_credential.hcp
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "external_user_flow" }
  to       = msgraph_resource.user_flow[0]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "external_app" }
  to       = azuread_application.external_hcp[0]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "external_sp" }
  to       = azuread_service_principal.external_hcp[0]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "external_fic" }
  to       = azuread_application_flexible_federated_identity_credential.external_hcp[0]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "external_graph_application" }
  to       = azuread_app_role_assignment.external_graph["Application.ReadWrite.All"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "external_graph_user" }
  to       = azuread_app_role_assignment.external_graph["User.ReadWrite.All"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "external_graph_eventlistener" }
  to       = azuread_app_role_assignment.external_graph["EventListener.ReadWrite.All"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "external_graph_delegatedpermissiongrant" }
  to       = azuread_app_role_assignment.external_graph["DelegatedPermissionGrant.ReadWrite.All"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "platform_rg" }
  to       = azurerm_resource_group.platform
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "platform_log" }
  to       = azurerm_log_analytics_workspace.platform
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "platform_env" }
  to       = azurerm_container_app_environment.platform
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "platform_vault" }
  to       = azurerm_key_vault.platform
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "vault_hcp" }
  to       = azurerm_role_assignment.vault["hcp"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "vault_gh" }
  to       = azurerm_role_assignment.vault["gh"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "vault_admin" }
  to       = azurerm_role_assignment.vault["admin"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "postgres_secret" }
  to       = azurerm_key_vault_secret.postgres
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "postgres" }
  to       = azurerm_postgresql_flexible_server.platform
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "postgres_rule" }
  to       = azurerm_postgresql_flexible_server_firewall_rule.azure
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "varset" }
  to       = tfe_variable_set.azure
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "var_TFC_AZURE_PROVIDER_AUTH" }
  to       = tfe_variable.env["TFC_AZURE_PROVIDER_AUTH"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "var_TFC_AZURE_RUN_CLIENT_ID" }
  to       = tfe_variable.env["TFC_AZURE_RUN_CLIENT_ID"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "var_ARM_SUBSCRIPTION_ID" }
  to       = tfe_variable.env["ARM_SUBSCRIPTION_ID"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "var_ARM_TENANT_ID" }
  to       = tfe_variable.env["ARM_TENANT_ID"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "var_TFC_AZURE_PROVIDER_AUTH_EXTERNAL" }
  to       = tfe_variable.env["TFC_AZURE_PROVIDER_AUTH_EXTERNAL"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "var_TFC_AZURE_RUN_CLIENT_ID_EXTERNAL" }
  to       = tfe_variable.env["TFC_AZURE_RUN_CLIENT_ID_EXTERNAL"]
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "var_deployer_object_id" }
  to       = tfe_variable.deployer_object_id
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "var_platform" }
  to       = tfe_variable.platform
  id       = each.value
}

import {
  for_each = { for k, v in var.adopt : k => v if k == "budget" }
  to       = azurerm_consumption_budget_subscription.previews
  id       = each.value
}

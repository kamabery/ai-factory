# `factory previews-env` reads `dotenv` and writes each setting into .env (contract, "Deploy"). Never HCP_ORG: setting
# it is what turns deploys on, and that stays your choice.
output "dotenv" {
  description = "The .env settings this setup provides."
  value = merge(
    {
      AZURE_GH_CLIENT_ID    = azuread_application.github.client_id
      AZURE_TENANT_ID       = var.tenant_id
      AZURE_SUBSCRIPTION_ID = var.subscription_id
      HCP_AZURE_VARSET      = tfe_variable_set.azure.id
      # The server `factory platform` starts and stops (spec 24).
      FACTORY_PLATFORM_SERVER_ID = azurerm_postgresql_flexible_server.platform.id
    },
    local.external ? {
      FACTORY_ENTRA_TENANT_ID    = var.external_tenant_id
      FACTORY_ENTRA_SUBDOMAIN    = split(".", data.azuread_domains.external[0].domains[0].domain_name)[0]
      FACTORY_ENTRA_USER_FLOW_ID = msgraph_resource.user_flow[0].id
    } : {},
  )
}

# A target with the auth add-on commits these in IaC/envs/{dev,staging,ephemeral}.tfvars (not secrets; J-022).
output "target_tfvars" {
  description = "The entra_external_* lines for an auth add-on target's tfvars."
  value = local.external ? join("\n", [
    "entra_external_tenant_id    = \"${var.external_tenant_id}\"",
    "entra_external_subdomain    = \"${split(".", data.azuread_domains.external[0].domains[0].domain_name)[0]}\"",
    "entra_external_user_flow_id = \"${msgraph_resource.user_flow[0].id}\"",
  ]) : null
}

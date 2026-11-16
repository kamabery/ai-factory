# The HCP variable set every target's project gets (HCP_AZURE_VARSET). The factory attaches it to each project; this
# owns only the set and its variables. Its variables are part of the canary fingerprint, so a change here makes the
# next deployable job wait for a canary.

resource "tfe_variable_set" "azure" {
  name        = var.varset_name
  description = "Azure and Entra External ID dynamic credentials for factory previews (factory/infra/previews). Attached per target project by factory."
}

locals {
  varset_env = merge(
    {
      TFC_AZURE_PROVIDER_AUTH = "true"
      TFC_AZURE_RUN_CLIENT_ID = azuread_application.hcp.client_id
      ARM_SUBSCRIPTION_ID     = var.subscription_id
      ARM_TENANT_ID           = var.tenant_id
    },
    local.external ? {
      TFC_AZURE_PROVIDER_AUTH_EXTERNAL = "true"
      TFC_AZURE_RUN_CLIENT_ID_EXTERNAL = azuread_application.external_hcp[0].client_id
    } : {},
  )
}

resource "tfe_variable" "env" {
  for_each = local.varset_env

  variable_set_id = tfe_variable_set.azure.id
  key             = each.key
  value           = each.value
  category        = "env"
}

# Each environment's Key Vault grants this identity Secrets User, so the workflow's schema step can read its
# connection string (its Contributor role is control-plane only).
resource "tfe_variable" "deployer_object_id" {
  variable_set_id = tfe_variable_set.azure.id
  key             = "deployer_object_id"
  value           = azuread_service_principal.github.object_id
  category        = "terraform"
  description     = "GitHub Actions identity; each environment's Key Vault grants it Secrets User (factory/infra/previews)"
}

resource "tfe_variable" "platform" {
  variable_set_id = tfe_variable_set.azure.id
  key             = "platform"
  value           = local.platform_hcl
  category        = "terraform"
  hcl             = true
}

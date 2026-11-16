# Easy Auth's app registration (contract, "Cloud mode (deployed)", Sign-in): assignment required, so only the users
# named reach the dashboard. Keyless: Easy Auth signs in to Entra as the managed identity, through a federated
# credential on the app (the override-use-mi-fic-assertion-client-id secret), so the app has no client secret.

locals {
  fic_secret = "override-use-mi-fic-assertion-client-id"
}

resource "azuread_application" "dashboard" {
  display_name     = "ai-factory dashboard"
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azurerm_client_config.current.object_id]

  web {
    implicit_grant {
      id_token_issuance_enabled = true
    }
  }

  lifecycle {
    # The callback is set below, once the dashboard's address is known.
    ignore_changes = [web[0].redirect_uris]
  }
}

resource "azuread_service_principal" "dashboard" {
  client_id                    = azuread_application.dashboard.client_id
  app_role_assignment_required = true
  owners                       = [data.azurerm_client_config.current.object_id]
}

# The default access role (the all-zero id): an app with no roles of its own.
resource "azuread_app_role_assignment" "signin" {
  for_each = toset(var.signin_user_object_ids)

  app_role_id         = "00000000-0000-0000-0000-000000000000"
  principal_object_id = each.value
  resource_object_id  = azuread_service_principal.dashboard.object_id
}

resource "azuread_application_federated_identity_credential" "dashboard" {
  application_id = azuread_application.dashboard.id
  display_name   = "factory-managed-identity"
  description    = "Easy Auth signs in as the deployed factory's managed identity"
  audiences      = ["api://AzureADTokenExchange"]
  issuer         = "https://login.microsoftonline.com/${var.tenant_id}/v2.0"
  subject        = azurerm_user_assigned_identity.factory.principal_id
}

resource "azuread_application_redirect_uris" "dashboard" {
  application_id = azuread_application.dashboard.id
  type           = "Web"
  redirect_uris  = ["${module.dashboard.fqdn_url}/.auth/login/aad/callback"]
}

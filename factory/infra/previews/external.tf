# The Entra External ID tenant previews sign in against (auth add-on only), so test accounts never land in your own
# directory. The tenant itself is made by hand (docs/DEPLOY.md, section 2): its admin is whoever creates it, so create
# it as the work account you will keep using. Inside it: the sign-in user flow, and the app a preview's Terraform run
# signs in as to create its app registration, QA users and sign-in wiring.

locals {
  external = var.external_tenant_id != null

  # Create the preview's app registration and QA users, attach the app to the user flow, and admin-consent its
  # delegated scopes (azuread_service_principal_delegated_permission_grant; J-022's apply was 403 without it).
  external_graph_roles = toset(local.external ? [
    "Application.ReadWrite.All", "User.ReadWrite.All", "EventListener.ReadWrite.All", "DelegatedPermissionGrant.ReadWrite.All",
  ] : [])
}

data "azuread_domains" "external" {
  count    = local.external ? 1 : 0
  provider = azuread.external

  only_initial = true
}

data "azuread_service_principal" "external_graph" {
  count    = local.external ? 1 : 0
  provider = azuread.external

  client_id = "00000003-0000-0000-c000-000000000000"
}

# Email + password sign-up and sign-in, collecting email and display name.
resource "msgraph_resource" "user_flow" {
  count    = local.external ? 1 : 0
  provider = msgraph.external

  url = "identity/authenticationEventsFlows"
  body = {
    "@odata.type" = "#microsoft.graph.externalUsersSelfServiceSignUpEventsFlow"
    displayName   = var.user_flow_name
    onAuthenticationMethodLoadStart = {
      "@odata.type"     = "#microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp"
      identityProviders = [{ id = "EmailPassword-OAUTH" }]
    }
    onInteractiveAuthFlowStart = {
      "@odata.type"   = "#microsoft.graph.onInteractiveAuthFlowStartExternalUsersSelfServiceSignUp"
      isSignUpAllowed = true
    }
    onAttributeCollection = {
      "@odata.type" = "#microsoft.graph.onAttributeCollectionExternalUsersSelfServiceSignUp"
      attributes = [
        { id = "email", displayName = "Email Address", description = "Email Address", userFlowAttributeType = "builtIn", dataType = "string" },
        { id = "displayName", displayName = "Display Name", description = "Display Name", userFlowAttributeType = "builtIn", dataType = "string" },
      ]
      attributeCollectionPage = {
        views = [{
          inputs = [
            { attribute = "email", label = "Email Address", inputType = "text", hidden = true, editable = false, writeToDirectory = true, required = true },
            { attribute = "displayName", label = "Display Name", inputType = "text", hidden = false, editable = true, writeToDirectory = true, required = false },
          ]
        }]
      }
    }
  }

  lifecycle {
    # Graph fills in and reshapes the flow's pages on create; what this sets is only the starting point.
    ignore_changes = [body]
  }
}

resource "azuread_application" "external_hcp" {
  count    = local.external ? 1 : 0
  provider = azuread.external

  display_name = "factory-hcp-terraform"

  lifecycle {
    ignore_changes = [owners, tags, notes, description]
  }
}

resource "azuread_service_principal" "external_hcp" {
  count    = local.external ? 1 : 0
  provider = azuread.external

  client_id = azuread_application.external_hcp[0].client_id
}

# Application permissions on Microsoft Graph, granted directly (the same as admin consent).
resource "azuread_app_role_assignment" "external_graph" {
  for_each = local.external_graph_roles
  provider = azuread.external

  app_role_id         = data.azuread_service_principal.external_graph[0].app_role_ids[each.value]
  principal_object_id = azuread_service_principal.external_hcp[0].object_id
  resource_object_id  = data.azuread_service_principal.external_graph[0].object_id
}

resource "azuread_application_flexible_federated_identity_credential" "external_hcp" {
  count    = local.external ? 1 : 0
  provider = azuread.external

  application_id             = azuread_application.external_hcp[0].id
  display_name               = "hcp-terraform"
  issuer                     = "https://app.terraform.io"
  audience                   = "api://AzureADTokenExchange"
  claims_matching_expression = local.hcp_claims
}

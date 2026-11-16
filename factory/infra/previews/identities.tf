# The subscription's tenant: the identities GitHub Actions and HCP Terraform sign in as. Both use OIDC, so there is
# no secret to store or rotate. Each preview's resource group does not exist until the job deploys, so the grants are
# on the whole subscription (docs/DEPLOY.md, "What it creates, and why").

locals {
  subscription_scope = "/subscriptions/${var.subscription_id}"

  # HCP's run token subject is organization:<org>:project:<p>:workspace:<w>:run_phase:<plan|apply>, and every
  # preview gets a new workspace, so the credential matches a pattern rather than one exact subject.
  hcp_claims = "claims['sub'] matches 'organization:${var.hcp_org}:project:*:workspace:*:run_phase:*'"
}

# GitHub Actions builds images into a preview's registry and runs its schema step. `factory deploy` adds one federated
# credential per target repo (repo:<owner>/<name>:environment:preview) under your az login; they are not managed here.
resource "azuread_application" "github" {
  display_name = "factory-github-actions"

  lifecycle {
    # The per-target federated credentials and anything set in the portal stay as they are.
    ignore_changes = [owners, tags, notes, description]
  }
}

resource "azuread_service_principal" "github" {
  client_id = azuread_application.github.client_id
}

resource "azurerm_role_assignment" "github_contributor" {
  scope                = local.subscription_scope
  role_definition_name = "Contributor"
  principal_id         = azuread_service_principal.github.object_id
  principal_type       = "ServicePrincipal"
}

# HCP Terraform runs each preview's Terraform with dynamic credentials.
resource "azuread_application" "hcp" {
  display_name = "factory-hcp-terraform"

  lifecycle {
    ignore_changes = [owners, tags, notes, description]
  }
}

resource "azuread_service_principal" "hcp" {
  client_id = azuread_application.hcp.client_id
}

# Contributor creates each preview's resource group and resources; User Access Administrator lets it grant roles inside
# them (a managed identity's registry pull), which Contributor cannot.
resource "azurerm_role_assignment" "hcp" {
  for_each = toset(["Contributor", "User Access Administrator"])

  scope                = local.subscription_scope
  role_definition_name = each.value
  principal_id         = azuread_service_principal.hcp.object_id
  principal_type       = "ServicePrincipal"
}

resource "azuread_application_flexible_federated_identity_credential" "hcp" {
  application_id             = azuread_application.hcp.id
  display_name               = "hcp-terraform"
  issuer                     = "https://app.terraform.io"
  audience                   = "api://AzureADTokenExchange"
  claims_matching_expression = local.hcp_claims
}

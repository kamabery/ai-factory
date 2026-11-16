variable "subscription_id" {
  description = "The subscription previews deploy into and bill to. Use one that holds only previews: the factory's identities get Contributor and User Access Administrator on all of it."
  type        = string
}

variable "tenant_id" {
  description = "The subscription's Entra tenant (AZURE_TENANT_ID)."
  type        = string
}

variable "hcp_org" {
  description = "The HCP Terraform organization (HCP_ORG). Azure trusts run tokens from this organization only."
  type        = string
}

variable "external_tenant_id" {
  description = "The Entra External ID tenant previews sign in with (auth add-on only), created by hand (docs/DEPLOY.md). Null: no External tenant objects."
  type        = string
  default     = null
}

variable "user_flow_name" {
  description = "The External tenant's sign-in user flow."
  type        = string
  default     = "preview-signin"
}

variable "location" {
  description = "Where the shared platform lives; as IaC/envs/ephemeral.tfvars, so a preview elsewhere keeps its own stack."
  type        = string
  default     = "centralus"
}

variable "platform_resource_group" {
  description = "The shared platform's resource group."
  type        = string
  default     = "rg-factory-platform"
}

variable "postgres_azure_rule_name" {
  description = "The Postgres firewall rule that lets Azure services in (0.0.0.0). An adopted server keeps the dated name az gave it."
  type        = string
  default     = "AllowAllAzureServicesAndResourcesWithinAzureIps"
}

variable "keyvault_admin_object_id" {
  description = "Who gets Key Vault Secrets Officer on the platform vault to write the Postgres password. Null: whoever applies."
  type        = string
  default     = null
}

variable "varset_name" {
  description = "The HCP variable set every target's project gets (HCP_AZURE_VARSET)."
  type        = string
  default     = "factory-azure"
}

variable "contact_email" {
  description = "Who the budget emails when spend crosses a threshold."
  type        = string
}

variable "monthly_budget" {
  description = "The monthly budget, in the subscription's billing currency."
  type        = number
  default     = 50
}

variable "budget_start_date" {
  description = "First day of the first budgeted month, as YYYY-MM-01T00:00:00Z. Azure refuses a past month on create."
  type        = string
  default     = "2026-11-01T00:00:00Z"
}

variable "adopt" {
  description = "Objects an earlier setup made, to import rather than create: a key from imports.tf to its import id. Empty on a fresh install."
  type        = map(string)
  default     = {}
}

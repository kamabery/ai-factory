variable "subscription_id" {
  description = "The subscription the factory runs in (the previews' own)."
  type        = string
}

variable "tenant_id" {
  description = "Its tenant: the managed identity, the dashboard's app registration and its users live here."
  type        = string
}

variable "location" {
  description = "As the preview platform's environment, which the dashboard and the runner run on."
  type        = string
  default     = "centralus"
}

variable "resource_group_name" {
  type    = string
  default = "rg-factory-cloud"
}

variable "container_app_environment_id" {
  description = "The preview platform's Container Apps environment (factory/infra/previews, cae-factory-platform)."
  type        = string
}

variable "foundry_account_id" {
  description = "The Foundry account factory/infra/llm made; the identity gets Foundry User on it and nothing wider."
  type        = string
}

variable "foundry_resource_name" {
  description = "FACTORY_FOUNDRY_RESOURCE: that account's name (factory/infra/llm's resource_name output)."
  type        = string
}

variable "image" {
  description = "The factory's image, ghcr.io/<owner>/ai-factory:<package version> (factory/container/Dockerfile)."
  type        = string
}

variable "github_owner" {
  description = "GITHUB_OWNER: the account the factory's GitHub App is installed on."
  type        = string
}

variable "signin_user_object_ids" {
  description = "The users assigned to the dashboard's app registration: the only ones Easy Auth lets in."
  type        = list(string)

  validation {
    condition     = length(var.signin_user_object_ids) > 0
    error_message = "Name at least one user, or no one can sign in to the dashboard."
  }
}

variable "settings" {
  description = "More environment for both containers: HCP_ORG and the previews-env settings when previews are on."
  type        = map(string)
  default     = {}
}

variable "monthly_budget" {
  description = "The resource group's monthly budget, in USD; the contact is mailed at 80% and 100%."
  type        = number
  default     = 25
}

variable "budget_start_date" {
  description = "The budget's first month, as YYYY-MM-01T00:00:00Z (Azure refuses a past month on create)."
  type        = string
}

variable "contact_email" {
  type = string
}

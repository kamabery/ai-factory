variable "subscription_id" {
  description = "The subscription that holds the Foundry account. Its Claude deployments bill here through Azure Marketplace."
  type        = string
}

variable "principal_ids" {
  description = "Entra object ids that run the factory in cloud mode (you, later a managed identity): Foundry User on the account."
  type        = list(string)
}

variable "deployments" {
  description = "Claude model id (also the deployment name) to Global Standard capacity. The defaults are the subscription's quota in eastus2 (az cognitiveservices usage list); billing is per token, not per unit."
  type        = map(number)
  default = {
    "claude-opus-5-5"   = 40
    "claude-sonnet-5-5" = 80
    "claude-haiku-5-5"  = 80
  }
}

variable "model_version" {
  description = "The pinned model version. 2 is hosted on Azure (model.capabilities.hostedOn in az cognitiveservices model list); 1 is hosted on Anthropic."
  type        = string
  default     = "2"
}

variable "provider_organization_name" {
  description = "The legal entity name Azure sends to Anthropic when it accepts the Claude Marketplace offer (modelProviderData.organizationName)."
  type        = string
}

variable "provider_country_code" {
  description = "Two-letter country code for the Marketplace attestation (modelProviderData.countryCode)."
  type        = string
  default     = "US"
  validation {
    condition     = can(regex("^[A-Z]{2}$", var.provider_country_code))
    error_message = "provider_country_code is a two-letter, upper-case country code, such as US."
  }
}

variable "provider_industry" {
  description = "Industry for the Marketplace attestation (modelProviderData.industry), lower case."
  type        = string
  default     = "technology"
  validation {
    condition     = contains(["technology", "finance", "healthcare", "education", "retail", "manufacturing", "government", "media", "other"], var.provider_industry)
    error_message = "provider_industry must be one of technology, finance, healthcare, education, retail, manufacturing, government, media, other (lower case)."
  }
}

variable "resource_group_name" {
  description = "Where the account lives."
  type        = string
  default     = "rg-factory-llm"
}

variable "location" {
  description = "Azure region. All three default models are Global Standard there."
  type        = string
  default     = "eastus2"
}

variable "tfc_azure_dynamic_credentials" {
  description = "Set by HCP Terraform. Do not set by hand."
  type = object({
    default = object({ client_id_file_path = string, oidc_token_file_path = string })
    aliases = map(object({ client_id_file_path = string, oidc_token_file_path = string }))
  })
  default = null
}

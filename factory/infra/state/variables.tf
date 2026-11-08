variable "subscription_id" {
  description = "The subscription that holds the factory's state account."
  type        = string
}

variable "principal_ids" {
  description = "Entra object ids that run the factory (you, later a managed identity): Storage Blob Data Owner on the container, and Storage Account Contributor for allow-ip."
  type        = list(string)
}

variable "ip_rules" {
  description = "Public IPv4 addresses allowed through the firewall at creation. Afterwards `factory.ps1 allow-ip` keeps them."
  type        = list(string)
  default     = []
}

variable "resource_group_name" {
  description = "Where the account lives; allow-ip assumes this name unless FACTORY_STATE_RG says otherwise."
  type        = string
  default     = "rg-factory-state"
}

variable "location" {
  description = "Azure region."
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

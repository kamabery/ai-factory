output "resource_name" {
  description = "FACTORY_FOUNDRY_RESOURCE for .env (or the deployed factory's environment)."
  value       = module.foundry_account.name
}

output "endpoint" {
  description = "The account's endpoint; Claude's Messages API is at https://<resource_name>.services.ai.azure.com/anthropic."
  value       = module.foundry_account.endpoint
}

output "deployments" {
  description = "The deployment names, which cloud mode pins Claude Code's aliases to by default."
  value       = keys(local.deployments)
}

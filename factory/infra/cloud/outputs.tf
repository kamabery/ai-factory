# `factory init cloud` reads `cloud` (contract, "Cloud mode (deployed)").
output "cloud" {
  description = "The vault init cloud copies this machine's secrets into, and the dashboard's address."
  value = {
    vault_uri     = module.vault.uri
    dashboard_url = module.dashboard.fqdn_url
  }
}

output "identity_principal_id" {
  description = "The deployed factory's managed identity."
  value       = azurerm_user_assigned_identity.factory.principal_id
}

output "runner_job_id" {
  value = local.job_id
}

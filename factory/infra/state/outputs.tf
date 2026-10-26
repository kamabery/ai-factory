output "blob_url" {
  description = "FACTORY_BLOB_URL for .env."
  value       = "${azurerm_storage_account.state.primary_blob_endpoint}${azurerm_storage_container.factory.name}"
}

output "storage_account" {
  description = "FACTORY_STORAGE_ACCOUNT for .env (allow-ip)."
  value       = azurerm_storage_account.state.name
}

output "resource_group" {
  description = "FACTORY_STATE_RG for .env, when it is not rg-factory-state."
  value       = azurerm_resource_group.state.name
}

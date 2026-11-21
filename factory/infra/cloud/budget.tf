# The cloud factory's own spend (Foundry tokens bill on the llm account, outside it).
resource "azurerm_consumption_budget_resource_group" "cloud" {
  name              = "budget-factory-cloud"
  resource_group_id = azurerm_resource_group.cloud.id
  amount            = var.monthly_budget
  time_grain        = "Monthly"

  time_period {
    start_date = var.budget_start_date
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThan"
    threshold_type = "Actual"
    contact_emails = [var.contact_email]
  }

  notification {
    enabled        = true
    threshold      = 100
    operator       = "GreaterThan"
    threshold_type = "Forecasted"
    contact_emails = [var.contact_email]
  }

  lifecycle {
    # Azure keeps the first month it was created for.
    ignore_changes = [time_period]
  }
}

# Previews auto-destroy after 24 hours, but a failed destroy leaves resources billing: an email when actual spend
# crosses 50%, 80% and 100%, and when forecast spend crosses 100%.

resource "azurerm_consumption_budget_subscription" "previews" {
  name            = "factory-previews"
  subscription_id = local.subscription_scope
  amount          = var.monthly_budget
  time_grain      = "Monthly"

  time_period {
    start_date = var.budget_start_date
  }

  dynamic "notification" {
    for_each = { actual50 = ["Actual", 50], actual80 = ["Actual", 80], actual100 = ["Actual", 100], forecast100 = ["Forecasted", 100] }

    content {
      enabled        = true
      operator       = "GreaterThan"
      threshold_type = notification.value[0]
      threshold      = notification.value[1]
      contact_emails = [var.contact_email]
    }
  }
}

# A monthly budget on the subscription the previews deploy into, with an email when actual spend
# crosses 50%, 80% and 100% and when forecast spend crosses 100%. Applied once, by hand, from this
# directory - it is the factory's backstop, not part of any target:
#
#   az login
#   terraform init
#   terraform apply -var subscription_id=<id> -var contact_email=<you>
#
# Local state on purpose: one resource that rarely changes, owned by the person who applies it.

terraform {
  required_version = ">= 1.9.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.14"
    }
  }
}

variable "subscription_id" {
  description = "The subscription the factory's previews deploy into."
  type        = string
}

variable "contact_email" {
  description = "Who is told when spend crosses a threshold."
  type        = string
}

variable "monthly_amount" {
  description = "The monthly budget, in the subscription's billing currency."
  type        = number
  default     = 50
}

variable "start_date" {
  description = "First day of the first budgeted month, as YYYY-MM-01T00:00:00Z. Azure refuses a past month."
  type        = string
  default     = "2026-11-01T00:00:00Z"
}

provider "azurerm" {
  features {}
  subscription_id = var.subscription_id
}

resource "azurerm_consumption_budget_subscription" "factory" {
  name            = "factory-previews"
  subscription_id = "/subscriptions/${var.subscription_id}"
  amount          = var.monthly_amount
  time_grain      = "Monthly"

  time_period {
    start_date = var.start_date
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

# 18: Cloud mode on Container Apps

**Status:** not started. **Branch:** `feature/18-cloud-mode`. **Depends on:** 13, 16, 17.

## Scope

- **Packaging:** `aspire publish` to Azure Container Apps, with Azure SQL (13), Key Vault, Blob for
  artifacts, and a managed identity. The IaC is Terraform with AVM modules (per the AVM decision),
  and `factory init cloud` deploys it.
- **Runner:** unattended on Claude in Microsoft Foundry through the managed identity (Azure AI User
  on the Foundry resource only; no keys). `Factory__Mode=cloud`. A subscription never powers cloud
  jobs.
- **Dashboard:** behind Entra sign-in (Easy Auth or the app's own OIDC), with the allowed users
  configured.
- **Per-job ephemeral containers** (Container Apps jobs) replace the temp work directories. This is
  the isolation unit hosted mode will need.
- Diagnostics go to Log Analytics; Azure Monitor tracks token usage next to the factory's cost
  events.

## Done when

`factory init cloud` on a clean subscription, then one idea from the cloud dashboard, runs end to
end unattended on Foundry with no API key anywhere, previews included. The eval is recorded.

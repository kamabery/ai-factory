# 18: Cloud mode on Container Apps

**Status:** done (2026-11-20), PR pending. A deployed factory runs in rg-factory-cloud on ai-factory 0.3.9: the dashboard behind Easy Auth, one Container Apps job execution per runner pass, Azure SQL + Blob + Key Vault, every model call on Foundry through the managed identity, and no key anywhere. Live proof: J-001 (ai-factory-hello, nothing to preview) merged; I-002 bootstrapped kamabery/booklist (J-002) and built J-003 to a live SQLite preview, merged as PR #2; canaries 10, 12 and 13 passed. Evals: good, good, mixed. Tests: unit 1125, integration 200 + 1 skipped (Windows), WSL 1123 / 197 + 4 skipped, Playwright smoke 5, Vitest 79, terraform test (cloud) 9. Follow-ups are roadmaps 21, 22 and 23. **Branch:** `feature/18-cloud-mode`. **Depends on:** 13, 16, 17.

## Scope

- **Packaging:** `aspire publish` to Azure Container Apps, with Azure SQL (13), Key Vault, Blob for
  artifacts, and a managed identity. The IaC is Terraform with AVM modules (per the AVM decision),
  and `factory init cloud` deploys it. The factory's container image is published to GHCR here (moved from 15,
  2026-11-15): Key Vault and the managed identity are what a container has in place of the OS secret store.
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

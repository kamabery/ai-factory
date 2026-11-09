# 16: `factory init previews`

**Status:** not started. **Branch:** `feature/16-init-previews`. **Depends on:** 09, 14. **Decision:** D4.

## Goal

One guided, idempotent, resumable command replaces `setup-azure.ps1`, `setup-platform.ps1` and the
manual steps in `docs/DEPLOY.md`.

## Scope

- `az login` and `terraform login`, then: the HCP organization and team token through its API (no
  copy-paste), the Azure identities, roles and federated credentials, the External tenant through
  Resource Manager (with the "creator is the admin" check), the user flow and Graph grants (a token
  for the External tenant, not `az rest`), the variable set, the budget, and the shared platform
  (~$18/month, said before it is applied).
- **Preflight checks from live lessons:** the personal-account alias trap (use a work account such as
  factory-admin), organization vs team token, `az` token-cache user switching, Entra replication lag.
- Writes its results to the secret store and settings, then runs `factory canary -Full`.
- `factory doctor` gains the preview checks.

## Done when

Run against a clean subscription it ends with a green canary, and a real job reaches `preview: up`
and is torn down. Running it a second time changes nothing. This is also 09's live check: the
preview comes from the .NET implementation, and its destroy leaves no resource group, checked in
Azure, not on the board. Deploys are back on (`HCP_ORG`) from here.

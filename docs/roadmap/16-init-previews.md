# 16: Previews setup as Terraform

**Status:** done (2026-11-16). Adopted live with no change to Azure (36 imported; second plan: No changes); `canary -Full` passed in 36 min and tore down; J-002 on todoapp-two reached `preview: up` from the .NET deploy (46 min, $1.75) and `destroy` left no resource group or workspace; the live check found and fixed an id collision across boards (checkout). Suites: unit 1070, integration 196 + 1 skipped (Windows), 193 + 4 skipped (WSL), coverage gates ok, `terraform test` 5, Playwright smoke 5. **Branch:** `feature/16-init-previews`. **Depends on:** 09, 14. **Decision:** D4.

Reshaped 2026-11-16. The original spec was a guided, resumable `factory init previews` for a stranger with an empty
subscription: HCP org and team token made through the API, preflights, a run against a new subscription. That is
dropped. The goal now is the narrower one: retire the last PowerShell by putting the preview setup into Terraform that
is tested with `terraform test` and proven against the live subscription **without changing it**.

## Goal

`factory/infra/previews` (Terraform, HCL) replaces `setup-azure.ps1`, `setup-platform.ps1` and `factory/infra/budget`.
The scripts go with it: `setup-azure.ps1`, `setup-platform.ps1`, `env-file.ps1`, `setup-azure.Tests.ps1`,
`setup-platform.Tests.ps1` and their fixtures (`az-setup-stub.ps1`, `az-platform-stub.ps1`, `github.ps1`), each Pester
case mapped in the PR to a `terraform test` run, a .NET test, or "covered by Terraform itself" with the reason, as in
spec 12. After 16 and 20, the repo has no PowerShell.

## Scope

- **What the Terraform owns:**
  - Main tenant: the app registrations `factory-github-actions` and `factory-hcp-terraform`, their service principals,
    Contributor (both) and User Access Administrator (HCP) on the subscription, HCP's flexible federated credential
    (`organization:<org>:project:*:workspace:*:run_phase:*`, Graph beta), and the resource provider registrations.
  - External tenant (`kamfactorypv`): the `preview-signin` user flow, the HCP app, its service principal, its Graph
    app roles (Application, User, EventListener and DelegatedPermissionGrant ReadWrite.All) and federated credential.
    Creating the tenant itself stays a documented manual step ("the creator is its admin").
  - The shared platform: `rg-factory-platform`, Log Analytics (0.2 GB/day cap), the Container Apps environment, the
    vault and its roles, Postgres B1ms. The admin password is a `random_password` stored as the vault secret
    `postgres-admin-password`, both with `ignore_changes`, so an adopted server keeps its password.
  - HCP: the variable set and its variables, `platform` included (`tfe` provider). The organization and team token
    stay manual.
  - The `factory-previews` budget (folding in `factory/infra/budget`).
- **State:** an HCP workspace `factory-setup` in `HCP_ORG`, execution mode local, so it runs under the human's
  `az login` (factory-admin) and `terraform login`.
- **Adopting what exists:** `import` blocks for every object the scripts made. The live proof is a `terraform plan`
  against the real subscription with **no changes**; nothing in Azure or HCP is modified.
- **`factory previews-env`** writes the Terraform outputs (`AZURE_GH_CLIENT_ID`, `AZURE_TENANT_ID`,
  `AZURE_SUBSCRIPTION_ID`, `HCP_AZURE_VARSET`, `FACTORY_ENTRA_*`) into `.env` (`terraform output -json` in
  `factory/infra/previews`, or `--from <json>`), by init's settings rule, and prints what changed. It never writes
  `HCP_ORG`: turning deploys on stays a human choice.
- **`factory doctor`** gains a `previews` check while `HCP_ORG` is set: the variable set exists with its required
  variables and the `AZURE_*` / `FACTORY_ENTRA_*` settings are present; HCP and `.env` only, no `az`. The `tools` check
  adds `gh` beside `terraform` while `HCP_ORG` is set.
- **Tool install:** the package ships the deploy code (`factory/infra`, the canary's inputs) and the
  `previews need a checkout install` refusal goes; a tool install's canary fingerprint names the package version in place
  of the deploy code's sources. The package is packed and installed by hand into a temp tool path (no test: packing
  publishes the runner and server, minutes per run); publishing to NuGet waits for Kurt.
- **Tests:** `cd factory/infra/previews; terraform test` with mock providers, its own row in TESTING.md's "Before a PR"
  (as `factory/infra/llm`), seen red before green. `previews-env` and the doctor check are unit-first .NET.

## Done when

`terraform test` passes; a plan against the live subscription, after import, shows no changes; and, as 09's live
check, one `factory canary -Full` and one real job on `kamabery/todoapp-two` (not `ai-factory-hello`, decided 2026-11-16: a console app with no IaC, so a job there would first bolt Azure onto the public sample) reach `preview: up` from the .NET
implementation and are torn down with no resource group left, checked in Azure. The live run uses its own state dir,
`.env`, secret prefix and local dir. The PowerShell and Pester are deleted with their mapping; the .NET suites pass on
Windows and in WSL; DEPLOY.md describes the Terraform path; this Status line and the roadmap README are updated.

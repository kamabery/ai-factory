# Spec 16: where each setup-script Pester case went

Spec 16 replaced `setup-azure.ps1` and `setup-platform.ps1` with `factory/infra/previews` (Terraform), and deleted their
Pester (`setup-azure.Tests.ps1`, `setup-platform.Tests.ps1`, 29 cases) with its fixtures (`az-setup-stub.ps1`,
`az-platform-stub.ps1`, `github.ps1`) and `env-file.ps1`. As in [spec 12's mapping](12-pester-mapping.md), each case
names what covers it now. Three kinds of answer:

- **tftest `<run>`**: an assertion in `factory/infra/previews/tests/previews.tftest.hcl` (mocked providers, offline).
- **.NET**: a unit test (`Factory.UnitTests`).
- **Terraform itself**: behaviour the scripts had to write by hand and Terraform has by construction (a resource is
  read before it is created, state remembers ids, a failed apply names the resource). The live proof covers these:
  the adoption plan imports all 36 objects the scripts made (planned 2026-11-16), and a second plan must show no changes.

## setup-azure.Tests.ps1

| Pester case | Covered by |
| --- | --- |
| creates both main-tenant apps with their service principals and roles | tftest `identities_and_their_roles` (both roles on the subscription, Contributor for GitHub Actions) |
| gives the HCP app a flexible federated credential for every workspace and run phase in the org | tftest `identities_and_their_roles` (the claims expression, issuer, audience) |
| writes the Azure values into .env over the commented lines and keeps the rest | .NET `PreviewsEnvCommandTests.Writes_each_setting_from_terraforms_dotenv_output_and_never_HCP_ORG` |
| prints the HCP variable set values | Terraform itself: the set is a resource now, not printed for hand entry; its values are tftest `the_shared_platform` and `the_external_tenant` (`varset_env`, `deployer_object_id`, `platform`) |
| prints the entra_* ids as tfvars lines for the target repo, not as variable set values | the `target_tfvars` output; tftest `no_external_tenant_makes_no_external_objects` and `the_external_tenant` keep them off the variable set |
| registers the resource providers the templates deploy to, Microsoft.App among them | Terraform itself: the azurerm provider's `resource_providers_to_register` (Microsoft.App among them), registered on every plan |
| prints deployer_object_id, the GitHub Actions identity each preview's Key Vault lets read its secrets | tftest `the_shared_platform` (`deployer_object_id` is the GitHub Actions service principal, a terraform variable) |
| sets up the External tenant: user flow, HCP app, Graph roles and federated credential | tftest `the_external_tenant` (the four Graph roles, the user flow, the credential) |
| reads the External subdomain from the tenant's initial domain when not passed | tftest `the_external_tenant` (`FACTORY_ENTRA_SUBDOMAIN` from the initial domain) |
| leaves az on the subscription it started on | Terraform itself: nothing switches az's account; the External tenant has its own provider with its own token |
| skips the External tenant without -ExternalTenantId | tftest `no_external_tenant_makes_no_external_objects` |
| creates nothing when everything already exists | Terraform itself; live: the second plan after adoption (no changes) |
| reads the subscription and HCP org from .env when not passed | dropped: they are Terraform variables (`local.auto.tfvars`), required, so a missing one fails before any call |
| creates the External tenant through Resource Manager, signs in to it and sets it up | dropped by the reshape (2026-11-16): the External tenant is made by hand (docs/DEPLOY.md, section 2); what goes in it is tftest `the_external_tenant` |
| reuses an External tenant that already exists | dropped with the case above; an existing tenant is the only kind now |
| stops rather than write a blank tenant id into .env | `tenant_id` is a required variable; .NET `PreviewsEnvCommandTests.Refuses_output_without_a_dotenv_map_and_changes_nothing` (no output, no write) |
| runs the az executable on PATH when FACTORY_AZ_CMD is unset | dropped: Terraform's providers sign in through the az CLI themselves; nothing runs az |
| refuses without an HCP org | `hcp_org` is a required variable |

## setup-platform.Tests.ps1

| Pester case | Covered by |
| --- | --- |
| creates the environment, Log Analytics, vault and Postgres server in the previews' region | tftest `the_shared_platform`; `var.location` defaults to `centralus` |
| sizes the server as the template does, with the Azure services rule, and caps the logs near the free 5 GB | tftest `the_shared_platform` (B1ms, 32 GB, the 0.2 GB/day cap); the firewall rule is `azurerm_postgresql_flexible_server_firewall_rule.azure` |
| names the vault and server after the subscription, the same on every run, since both names are global | tftest `the_shared_platform` (the subscription hash) |
| grants HCP Secrets Officer and GitHub Actions Secrets User on the vault, and no preview-wide more | tftest `the_shared_platform` |
| stores the admin password in the vault under the name deploy.yml reads, and never prints it | tftest `the_shared_platform` (40 alphanumerics, the vault secret is the server's password); Terraform marks it sensitive, so it is never printed |
| waits for the vault role to land before giving up on the secret | Terraform itself: the secret `depends_on` the applier's role assignment, and azurerm retries a 403 while RBAC propagates |
| does not put the password in a failure message | Terraform itself: a sensitive value is redacted in every diagnostic |
| puts the platform on the HCP variable set as an HCL terraform variable, which the template reads | tftest `the_shared_platform` (`platform`, HCL, in the order previews read); live: the adoption plan imports it with no diff, so the value is byte-identical |
| run again creates nothing, keeps the password, and leaves the variable set as it was | Terraform itself (`ignore_changes` on the password); live: the second plan after adoption (no changes) |
| updates the variable on the set when the platform changed | Terraform itself: `tfe_variable.platform` follows the platform's attributes |
| says to run setup-azure.ps1 first when its identities are missing | Terraform itself: the vault roles reference the identities in the same configuration, so there is no "first" |

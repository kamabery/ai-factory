# Setting up deploys (ephemeral previews)

With deploys on, every job is deployed to a throwaway Azure environment between review and approve:
the approver smoke-tests it, you QA it from Ready for review (**Launch**, **QA login**), and it is
torn down when the job is merged or discarded, when you press **Tear down**, or after 24 hours by
HCP Terraform. A rework keeps it, and the next round deploys over it. Production is never deployed. The rules are in
[FACTORY-CONTRACT.md](FACTORY-CONTRACT.md#deploy); this is the one-time setup.

Deploys are **off** until `.env` has `HCP_ORG`. Everything else keeps working without it.

Everything in Azure, Entra and the HCP variable set is Terraform: `factory/infra/previews`, applied by you, with its
state in the HCP workspace `factory-setup` (execution mode local, so it runs here under your `az login`). By hand: the
HCP organization and its team token (section 1) and, for the auth add-on, the External tenant itself (section 2).
`factory previews-env` then copies the results into `.env`, and `factory doctor` checks them.

## Every value, and where it comes from

| Value | Goes in | Where you get it | Section |
| --- | --- | --- | --- |
| `HCP_ORG` | `.env`, `hcp_org` | the organization name you create at app.terraform.io | 1 |
| `HCP_TOKEN`, `HCP_TEAM_TOKEN` | the secret store | one owners team token, in both: Settings → API tokens → Team tokens | 1 |
| External tenant ID | `external_tenant_id` | the External tenant's **Overview** page | 2 |
| `subscription_id`, `tenant_id`, `contact_email` | `factory/infra/previews/local.auto.tfvars` (gitignored) | `az account show`, your email | 3 |
| `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `AZURE_GH_CLIENT_ID`, `HCP_AZURE_VARSET`, `FACTORY_ENTRA_*` | `.env` | written by `factory previews-env` | 3 |
| `entra_external_*` (auth add-on only) | the target repo's `IaC/envs/*.tfvars` | `terraform output target_tfvars` | 3 |
| GitHub App permissions | `factory/github-app.json` (unchanged) | already asked for by the App `factory init` creates | 5 |
| `FACTORY_TEMPLATES_REPO` | `.env` | `owner/name` of your template-projects repo on GitHub | 6 |

## 1. HCP Terraform: organization and tokens

HCP Terraform runs each preview's Terraform and holds its state, and holds this setup's state too. `factory` drives it
over the API; the workflow in each target queues runs.

1. **Organization → `HCP_ORG`.** Sign in at https://app.terraform.io and create an organization. Its
   name, as it appears in the URL `app.terraform.io/app/<org>`, is `HCP_ORG`. Azure will trust tokens
   from this organization only (section 3).
2. **Team token → `HCP_TOKEN` and `HCP_TEAM_TOKEN`.**
   https://app.terraform.io/app/<org>/settings/authentication-tokens?tabIndex=1 (**Settings** →
   **Security** → **API tokens** → **Team tokens**) → **Create a team token**: team **owners** (the only
   team on the Free plan), description `ai-factory`, 90 days. Copy it when shown; it isn't shown
   again, and creating another replaces it. The same token is both secrets (`factory init --move-env-secrets` moves
   them from `.env` into the secret store).
   - `HCP_TOKEN`: `factory` creates each target's project and workspaces, attaches the variable
     set, and queues destroy runs.
   - `HCP_TEAM_TOKEN`: becomes each target's `TF_API_TOKEN` secret, which its workflow uses to
     queue runs.
   Not an organization token: HCP's own page says those "can't perform plans and applies", so
   tear-down would fail.
3. **`terraform login`**, once, for this machine's Terraform (the setup's state and the `tfe` provider). Or set
   `TF_TOKEN_app_terraform_io` (and `TFE_TOKEN`) to the team token for one shell.

## 2. Entra External ID: the tenant previews sign in with

Only targets with the auth add-on use it; skip this section until you have one. Preview users sign
in against a separate customer-facing tenant, so test accounts never land in your own directory.

**The tenant's admin is whoever creates it.** Create it as the work account you will keep using (factory-admin, say),
never a personal Microsoft account. If that account later loses the name the tenant knows it by (an alias removed, the
account deleted), nobody can sign in to the tenant again; `az login` answers "You can't sign in here with a personal
account", and the only way forward is a new tenant.

You need the **Tenant Creator** role or higher on the subscription.

1. In the [Microsoft Entra admin center](https://entra.microsoft.com), browse to **Entra ID** →
   **Overview** → **Manage tenants**, and select **Create**.
2. Select **External**, then **Continue**. A first external tenant can be a 30-day trial with no
   subscription; for previews choose **Use Azure Subscription**.
3. **Basics:** a **Tenant Name** (e.g. *Factory Previews*), a **Domain Name** (e.g.
   *factorypreviews*; this becomes `factorypreviews.onmicrosoft.com`) and a **Country/Region**
   (can't be changed later).
4. **Add a subscription:** the subscription the previews bill to, and a resource group (create one if
   needed). **Review + Create** → **Create**. Creation can take up to 30 minutes; watch
   **Notifications**.
5. Note its **Tenant ID**: **Settings** (gear, top bar) → **Directories + subscriptions** → **Switch**
   to the new directory → **Entra ID** → **Overview**.
6. Sign `az` in to it as the same account: `az login --tenant <External tenant ID> --allow-no-subscriptions`. Entra
   can take a few minutes to replicate a new tenant; a sign-in refused right after creation usually works a little
   later. Then switch back: `az account set --subscription <subscription ID>`.

The Terraform does the rest inside it: the user flow, the app HCP signs in as, and its permissions.

## 3. The setup (`factory/infra/previews`)

Sign `az` in to your Azure tenant as that same work account, and pick the subscription the previews deploy into and
bill to. `az account show` should name the work account, not a personal one: `az` keeps one token cache for every
account you have signed in with, and a later `az login` as someone else changes who Terraform runs as.

```powershell
az login --tenant <your Azure tenant>
az account set --subscription <subscription ID>
az account show --query "{user:user.name, subscription:id, tenant:tenantId}"
```

Write `factory/infra/previews/local.auto.tfvars` (gitignored):

```hcl
subscription_id    = "<subscription ID>"
tenant_id          = "<its tenant ID>"
hcp_org            = "<HCP_ORG>"
external_tenant_id = "<External tenant ID>"   # auth add-on only; leave out otherwise
contact_email      = "<you>"                  # the budget's alerts
```

Then, from that folder, with `TF_CLOUD_ORGANIZATION` set to your `HCP_ORG`:

```powershell
$env:TF_CLOUD_ORGANIZATION = '<HCP_ORG>'
terraform init                 # creates the factory-setup workspace; set its Execution Mode to Local in HCP once
terraform plan                 # read it: the platform is about $18 a month (below)
terraform apply
factory previews-env           # AZURE_*, HCP_AZURE_VARSET and FACTORY_ENTRA_* into .env
terraform output target_tfvars # auth add-on targets commit these in IaC/envs/{dev,staging,ephemeral}.tfvars
```

Then set `HCP_ORG` in `.env` last: it is what turns deploys on. `factory doctor` checks the variable set and the
settings (its `previews` line). Re-applying changes nothing unless something drifted.

What it creates, and why:

| In | Object | Why |
| --- | --- | --- |
| Your tenant | App registration `factory-github-actions` and its service principal; appId → `AZURE_GH_CLIENT_ID` | GitHub Actions builds images into the preview's registry and runs the schema step, so it needs an Azure identity. It signs in with OIDC, so there is no secret to store or rotate |
| Your subscription | **Contributor** for `factory-github-actions` | Each preview lives in its own resource group (`rg-<project>-j<NNN>`) that doesn't exist until the job deploys, so the grant can't be scoped to a group up front. Contributor can build and deploy but can't grant access to others |
| Your tenant | App registration `factory-hcp-terraform` and its service principal; appId → `TFC_AZURE_RUN_CLIENT_ID` | HCP Terraform runs the preview's Terraform with dynamic credentials (OIDC), again no secret |
| Your subscription | **Contributor** and **User Access Administrator** for `factory-hcp-terraform` | Contributor creates each preview's resource group and resources; User Access Administrator lets it grant roles inside them (managed identity → registry pull, for example), which Contributor can't |
| Your tenant | Flexible federated credential `hcp-terraform` on `factory-hcp-terraform` | Trusts tokens from `https://app.terraform.io` for your organization only. HCP's token subject is `organization:<org>:project:<p>:workspace:<w>:run_phase:<plan\|apply>`, and every preview gets a new workspace, so it matches `organization:<org>:project:*:workspace:*:run_phase:*` rather than one exact subject |
| Your subscription | The resource providers the templates deploy to (Microsoft.App among them) | azurerm registers only its own core set, and a new subscription then fails the Container Apps environment (canary-6) |
| External tenant | User flow `preview-signin` (email + password, sign-up allowed) → `FACTORY_ENTRA_USER_FLOW_ID` | The sign-in experience preview apps are attached to |
| External tenant | App registration `factory-hcp-terraform`, its service principal and federated credential → `TFC_AZURE_RUN_CLIENT_ID_EXTERNAL` | A preview's Terraform run creates its app registration, test users and sign-in wiring in the External tenant, signing in the same way |
| External tenant | Microsoft Graph **Application.ReadWrite.All**, **User.ReadWrite.All**, **EventListener.ReadWrite.All**, **DelegatedPermissionGrant.ReadWrite.All**, granted (admin consent) | Create the preview's app registration, its QA users, attach the app to the user flow, and admin-consent its delegated scopes (`azuread_service_principal_delegated_permission_grant`; without it the apply fails 403, J-022) |
| `rg-factory-platform` | The shared platform (below) | Previews are about twice as fast to create and six times to destroy |
| HCP | Variable set `factory-azure` → `HCP_AZURE_VARSET`: `TFC_AZURE_PROVIDER_AUTH`, `TFC_AZURE_RUN_CLIENT_ID`, `ARM_SUBSCRIPTION_ID`, `ARM_TENANT_ID`, (`TFC_*_EXTERNAL`), `deployer_object_id`, `platform` | Every preview's Terraform reads them. The factory attaches the set to each target's project. The three `entra_external_*` ids are not on it: a target commits them in its tfvars, one source (J-022) |
| Your subscription | Budget `factory-previews` ($50 a month; email at 50, 80 and 100% actual and 100% forecast) | Previews auto-destroy after 24 hours, but a failed destroy leaves resources billing |

The GitHub Actions app has no federated credential here, so it can't sign in from anywhere until a target deploys:
`factory deploy` adds one per target repo (`repo:<owner>/<name>:environment:preview`) under **your** `az login`, so
you need the right to update that app registration. Terraform ignores them.

Contributor and User Access Administrator on a whole subscription are broad. Use a subscription that
holds only previews, so the factory's identities can't touch anything else.

Other variables (defaults in `variables.tf`): `user_flow_name`, `location` (`centralus`), `platform_resource_group`,
`varset_name`, `monthly_budget`, `budget_start_date`, `keyvault_admin_object_id`.

### Adopting an existing setup

Objects made earlier (by the old `setup-azure.ps1` / `setup-platform.ps1`, or in the portal) are imported, not
created: name each in `adopt` in `local.auto.tfvars`, keyed as in `imports.tf`, with its provider's import id, and
`terraform plan` shows "to import" with no changes to Azure. For example:

```hcl
adopt = {
  github_app         = "/applications/<object id>"
  github_sp          = "/servicePrincipals/<object id>"
  github_contributor = "/subscriptions/<sub>/providers/Microsoft.Authorization/roleAssignments/<name>"
  hcp_fic            = "/applications/<object id>/federatedIdentityCredentials/<credential id>"
  external_graph_user = "/servicePrincipals/<Graph's service principal in the External tenant>/appRoleAssignedTo/<assignment id>"
  external_user_flow = "/identity/authenticationEventsFlows/<flow id>"
  postgres_secret    = "https://<vault>.vault.azure.net/secrets/postgres-admin-password/<version>"
  varset             = "varset-..."
  var_platform       = "<HCP_ORG>/varset-.../var-..."
  # ... one per object
}
```

An adopted Postgres server keeps its password (the vault's), and an az-made firewall rule keeps its dated name
(`postgres_azure_rule_name`). Once imported, the blocks are no-ops.

## 4. The shared platform

`j<NNN>` previews share one Container Apps environment, Log Analytics workspace, Key Vault and Postgres
server in `rg-factory-platform`, so a fresh preview takes about 13 to 16 minutes rather than 31, and a destroy
about 4 rather than 26 (measured on canaries 22 and 25; see `docs/PLAN-J022-LESSONS.md`, Phase 7).

**Cost: about $18 a month**, nearly all of it the B1ms Postgres server with 32 GB ($14.02 + $4.16,
`centralus` list prices, 2026-11-06). The environment has no fixed charge on the Consumption plan and
the vault costs pennies. Log Analytics stops ingesting at 0.2 GB a day, near its free 5 GB a month,
because one noisy preview now spends for all of them. The price list also has a $0.10/hour "Environment
Management Hour" meter (about $73 a month) that the billing docs do not name or apply here; check the
first bill for it.

**What is outside this Terraform, and why:**

| What | Done by | Why not here |
| --- | --- | --- |
| Each preview's Postgres login | `deploy.yml` (`psql`, from the GitHub runner) | HCP's runners sit outside Azure and the firewall keeps them out; the runner has already been let in for the schema step |
| Dropping that login | the next `deploy.yml` run | A destroy runs in HCP too, so each deploy drops the `preview_*` roles whose databases have gone. A stale role owns nothing, and its database is already gone |

**What previews share:**
- Logs, and the environment's limits.
- One server of 1 vCore and 2 GiB. A preview has its own databases and login, and `PUBLIC` is revoked on
  them, so it cannot reach another's.
- One vault. An app's identity is granted only its own secrets. The workflow's identity can read every
  secret, including `postgres-admin-password`, because it creates the logins.

A fresh preview no longer proves a from-scratch create; `factory canary -Full` does. dev, staging, a
preview in another region and scaffolds made before this keep a full stack of their own.

To remove the platform: remove `platform.tf`'s resources and the `platform` variable from the Terraform (or
`terraform destroy -target` them) and apply. Previews go back to full stacks.

### A 500 on an idle preview

Previews scale to zero after about 5 idle minutes. The first request through the Static Web App then
answers **500 `Backend call failure`** after about 45 s, and the next ones work (J-027, 2026-11-07). That
is a cold start, not a fault, so reload. Scale-to-zero stays because it keeps idle previews free. The
factory's smoke and the agents' preview checks retry, so it doesn't fail a job.

## 5. GitHub App permissions

The deploy stage pushes workflows, creates each target's `preview` environment and its variables,
sets the `TF_API_TOKEN` secret and dispatches the workflow. The GitHub App that `factory init` creates
already asks for **Workflows**, **Actions**, **Environments**, **Secrets** and **Variables** (read and write),
so there is nothing to add. Details in [GITHUB-APP.md](GITHUB-APP.md). `gh` must be
installed; the factory runs it with the token in its environment only.

## 6. `.env`

`FACTORY_TEMPLATES_REPO` is the GitHub `owner/name` of your template-projects repo, where the
Lessons tab proposes template fixes. From its local clone:
`gh repo view --json nameWithOwner -q .nameWithOwner`.

```dotenv
HCP_ORG=your-org              # section 1; set it last: it turns deploys on
HCP_AZURE_VARSET=varset-...   # section 3, written by factory previews-env
AZURE_GH_CLIENT_ID=...        # section 3, written by factory previews-env
AZURE_TENANT_ID=...           # section 3, written by factory previews-env
AZURE_SUBSCRIPTION_ID=...     # section 3, written by factory previews-env
FACTORY_ENTRA_TENANT_ID=...   # section 3, written by factory previews-env (auth add-on)
FACTORY_ENTRA_SUBDOMAIN=...
FACTORY_ENTRA_USER_FLOW_ID=...
FACTORY_TEMPLATES_REPO=you/template-projects
```

`HCP_TOKEN` and `HCP_TEAM_TOKEN` belong in the secret store, not here (`factory doctor` warns).

## Check it

Approve a small job against a scaffolded target and watch its card: Building shows `deploy r1 · 1/3`,
then Ready for review shows `preview: up` with Launch. A failed attempt leaves
`.factory-runs/jobs/<id>/deploy-<n>.log` with the GitHub and HCP logs; `factory deploy-log <id>`
prints the latest.

Targets scaffolded before HCP deploys existed are brought forward by the devops agent
(`aspire-addon infra adopt`) on their first job, so they need nothing by hand.

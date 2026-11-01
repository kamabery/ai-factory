# Setting up deploys (ephemeral previews)

With deploys on, every job is deployed to a throwaway Azure environment between review and approve:
the approver smoke-tests it, you QA it from Ready for review (**Launch**, **QA login**), and it is
torn down when the job is merged or discarded, when you press **Tear down**, or after 24 hours by
HCP Terraform. A rework keeps it, and the next round deploys over it. Production is never deployed. The rules are in
[FACTORY-CONTRACT.md](FACTORY-CONTRACT.md#deploy); this is the one-time setup.

Deploys are **off** until `.env` has `HCP_ORG`. Everything else keeps working without it.

Everything in Azure and Entra, including the Entra External ID tenant, is done by
`factory/scripts/setup-azure.ps1`. The browser is needed for the HCP organization, its tokens and
variable set, editing the GitHub PAT, and the script's sign-in to the new tenant.

## Every value, and where it comes from

Work through the sections in order: later values depend on earlier ones.

| Value | Goes in | Where you get it | Section |
| --- | --- | --- | --- |
| `HCP_ORG` | `.env`, `setup-azure.ps1 -HcpOrg` | the organization name you create at app.terraform.io | 1 |
| `HCP_TOKEN`, `HCP_TEAM_TOKEN` | `.env` | one owners team token, in both: Settings → API tokens → Team tokens | 1 |
| External tenant ID | `setup-azure.ps1 -ExternalTenantId` | the External tenant's **Overview** page | 2 |
| `AZURE_SUBSCRIPTION_ID` | `.env`, `setup-azure.ps1 -SubscriptionId` | `az account list -o table` | 3 |
| `AZURE_TENANT_ID`, `AZURE_GH_CLIENT_ID` | `.env` | written by `setup-azure.ps1` | 3 |
| Variable set values (`TFC_*`, `ARM_*`, `deployer_object_id`) | HCP variable set | printed by `setup-azure.ps1` | 3 → 4 |
| `entra_external_*` (auth add-on only) | the target repo's `IaC/envs/*.tfvars` | printed by `setup-azure.ps1` | 4 |
| `HCP_AZURE_VARSET` | `.env` | the `varset-...` id in the variable set's URL | 4 |
| `GITHUB_TOKEN` (more permissions) | `.env` (unchanged) | edit the existing GitHub PAT | 5 |
| `contact_email` | budget `terraform apply` | your email | 6 |
| `FACTORY_TEMPLATES_REPO` | `.env` | `owner/name` of your template-projects repo on GitHub | 7 |

## 1. HCP Terraform: organization and tokens

HCP Terraform runs each preview's Terraform and holds its state. `factory.ps1` drives it over the
API; the workflow in each target queues runs.

1. **Organization → `HCP_ORG`.** Sign in at https://app.terraform.io and create an organization. Its
   name, as it appears in the URL `app.terraform.io/app/<org>`, is `HCP_ORG`. Azure will trust tokens
   from this organization only (section 3).
2. **Team token → `HCP_TOKEN` and `HCP_TEAM_TOKEN`.**
   https://app.terraform.io/app/<org>/settings/authentication-tokens?tabIndex=1 (**Settings** →
   **Security** → **API tokens** → **Team tokens**) → **Create a team token**: team **owners** (the only
   team on the Free plan), description `claude-factory`, 90 days. Copy it when shown; it isn't shown
   again, and creating another replaces it. Put the same token in both lines.
   - `HCP_TOKEN`: `factory.ps1` creates each target's project and workspaces, attaches the variable
     set, and queues destroy runs.
   - `HCP_TEAM_TOKEN`: becomes each target's `TF_API_TOKEN` secret, which its workflow uses to
     queue runs.
   Not an organization token: HCP's own page says those "can't perform plans and applies", so
   tear-down would fail.

## 2. Entra External ID: the tenant previews sign in with

Only targets with the auth add-on use it; skip this section until you have one. Preview users sign
in against a separate customer-facing tenant, so test accounts never land in your own directory.

**Scripted (recommended):** pass `-CreateExternalTenant <subdomain> -ExternalResourceGroup <rg>` to
`setup-azure.ps1` (section 3). It creates `<subdomain>.onmicrosoft.com` as an Azure resource
(`Microsoft.AzureActiveDirectory/ciamDirectories`, `-ExternalLocation` default `United States`,
`-ExternalCountryCode` default `US`, neither changeable later), waits for it, and opens a browser
for you to sign in to it. The subdomain must be globally unique.

**The tenant's admin is whoever creates it.** Create it as the account you will keep using. If that
account later loses the name the tenant knows it by (an alias removed, the account deleted), nobody
can sign in to the tenant again; `az login` answers "You can't sign in here with a personal
account", and the only way forward is a new tenant.

**By hand**, if you prefer the portal (you need the **Tenant Creator** role or higher on the
subscription):

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

`setup-azure.ps1` does the rest inside it: the user flow, the app HCP signs in as, and its permissions.

## 3. Azure: identities and permissions (`setup-azure.ps1`)

Sign `az` in to your Azure tenant, as the same account throughout, and pick the subscription the
previews deploy into and bill to:

```powershell
az login --tenant <your Azure tenant>                                   # or plain az login
az account list --query "[].{name:name, id:id}" -o table                 # the id is the subscription ID
```

Then run, from the repo root, one of:

```powershell
# creating the External tenant too (section 2, scripted)
pwsh factory/scripts/setup-azure.ps1 -SubscriptionId <subscription ID> -HcpOrg <HCP_ORG> -CreateExternalTenant <subdomain> -ExternalResourceGroup <rg>
# with an External tenant you already have
pwsh factory/scripts/setup-azure.ps1 -SubscriptionId <subscription ID> -HcpOrg <HCP_ORG> -ExternalTenantId <External tenant ID>
# no auth add-on yet
pwsh factory/scripts/setup-azure.ps1 -SubscriptionId <subscription ID> -HcpOrg <HCP_ORG>
```

When the script reaches the External tenant and `az` isn't signed in to it, it runs
`az login --tenant <id> --allow-no-subscriptions` and a browser opens; sign in as the same account. The script finds each object before it
creates it, so it is safe to re-run, including over anything you made by hand. It leaves `az` on the
subscription it started on. It writes `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID` and
`AZURE_GH_CLIENT_ID` into `.env`, and prints the variable set values for section 4.

What it creates, and why:

| In | Object | Why |
| --- | --- | --- |
| Your tenant | App registration `factory-github-actions` and its service principal; appId → `AZURE_GH_CLIENT_ID` | GitHub Actions builds images into the preview's registry and runs the schema step, so it needs an Azure identity. It signs in with OIDC, so there is no secret to store or rotate |
| Your subscription | **Contributor** for `factory-github-actions` | Each preview lives in its own resource group (`rg-<project>-j<NNN>`) that doesn't exist until the job deploys, so the grant can't be scoped to a group up front. Contributor can build and deploy but can't grant access to others |
| Your tenant | App registration `factory-hcp-terraform` and its service principal; appId → `TFC_AZURE_RUN_CLIENT_ID` | HCP Terraform runs the preview's Terraform with dynamic credentials (OIDC), again no secret |
| Your subscription | **Contributor** and **User Access Administrator** for `factory-hcp-terraform` | Contributor creates each preview's resource group and resources; User Access Administrator lets it grant roles inside them (managed identity → registry pull, for example), which Contributor can't |
| Your tenant | Federated credential `hcp-terraform` on `factory-hcp-terraform` | Trusts tokens from `https://app.terraform.io` for your organization only. HCP's token subject is `organization:<org>:project:<p>:workspace:<w>:run_phase:<plan\|apply>`, and every preview gets a new workspace, so this is a *flexible* federated credential (Graph beta) matching `organization:<org>:project:*:workspace:*:run_phase:*` rather than one exact subject |
| External tenant | User flow `preview-signin` (email + password, sign-up allowed) → `entra_external_user_flow_id` | The sign-in experience preview apps are attached to |
| External tenant | App registration `factory-hcp-terraform`, its service principal and federated credential → `TFC_AZURE_RUN_CLIENT_ID_EXTERNAL` | A preview's Terraform run creates its app registration, test users and sign-in wiring in the External tenant, signing in the same way |
| External tenant | Microsoft Graph **Application.ReadWrite.All**, **User.ReadWrite.All**, **EventListener.ReadWrite.All**, **DelegatedPermissionGrant.ReadWrite.All**, granted (admin consent) | Create the preview's app registration, its QA users, attach the app to the user flow, and admin-consent its delegated scopes (`azuread_service_principal_delegated_permission_grant`; without it the apply fails 403, J-022) |

The GitHub Actions app has no federated credential yet, so it can't sign in from anywhere.
`factory.ps1 deploy` adds one per target repo (`repo:<owner>/<name>:environment:preview`) under
**your** `az login`, so you need the right to update that app registration. The subject limits
sign-in to that repo's `preview` environment.

Contributor and User Access Administrator on a whole subscription are broad. Use a subscription that
holds only previews, so the factory's identities can't touch anything else.

Options: `-UserFlowName` (default `preview-signin`), `-ExternalSubdomain` (default: read from the
External tenant's initial domain), `-EnvFile` (default the repo's `.env`). `-SubscriptionId` and
`-HcpOrg` default to `AZURE_SUBSCRIPTION_ID` and `HCP_ORG` in `.env` when set.

To see any value later:

```powershell
az ad app list --display-name factory-github-actions --query "[].appId" -o tsv    # AZURE_GH_CLIENT_ID
az ad app list --display-name factory-hcp-terraform --query "[].appId" -o tsv     # TFC_AZURE_RUN_CLIENT_ID (per tenant)
```

The appId is the portal's **Application (client) ID**. The app's object ID and the service
principal's object ID are different GUIDs, and none of them goes in `.env` but the appId.

## 4. HCP Terraform: the variable set

**Organization settings** → **Variable sets** → **Create variable set**, scoped to *specific
projects* with none selected (the factory attaches it to each target's project). Add what
`setup-azure.ps1` printed:

| Variable | Category | Value |
| --- | --- | --- |
| `TFC_AZURE_PROVIDER_AUTH` | env | `true` |
| `TFC_AZURE_RUN_CLIENT_ID` | env | printed |
| `ARM_SUBSCRIPTION_ID` | env | printed |
| `ARM_TENANT_ID` | env | printed |
| `deployer_object_id` | terraform | printed: the GitHub Actions identity, which each environment's Key Vault lets read its secrets (the workflow's schema step) |
| `TFC_AZURE_PROVIDER_AUTH_EXTERNAL` | env | `true` (auth add-on only) |
| `TFC_AZURE_RUN_CLIENT_ID_EXTERNAL` | env | printed (auth add-on only) |

The three `entra_external_*` ids are not in the variable set. A target with the auth add-on commits
them, as printed, in `IaC/envs/dev.tfvars`, `staging.tfvars` and `ephemeral.tfvars`. That is one
source, and the workflow's `-var-file` would override the set anyway (J-022). They are not secrets.

Save it. Its id is in the URL, `.../settings/varsets/varset-XXXXXXXX` → `HCP_AZURE_VARSET`. Or
list them with the team token:

```powershell
$h = @{ Authorization = "Bearer <HCP_TOKEN>" }
(Invoke-RestMethod "https://app.terraform.io/api/v2/organizations/<HCP_ORG>/varsets" -Headers $h).data |
  ForEach-Object { "$($_.id)  $($_.attributes.name)" }
```

## 5. GitHub PAT

The deploy stage pushes workflows, creates each target's `preview` environment and its variables,
sets the `TF_API_TOKEN` secret and dispatches the workflow. Edit the factory's existing PAT (GitHub →
**Settings** → **Developer settings** → **Fine-grained tokens** → *the token* → **Edit**) and add
**Workflows**, **Actions**, **Environments**, **Secrets** and **Variables** (read and write). Editing
keeps the token value, so `GITHUB_TOKEN` in `.env` doesn't change. Details in
[GITHUB-PAT.md](GITHUB-PAT.md). `gh` must be installed; the factory runs it with the PAT in its
environment only.

## 6. A budget alert

Previews auto-destroy after 24 hours, but a failed destroy leaves resources billing. A monthly
budget on the subscription emails you before that adds up. Once, from `factory/infra/budget`:

```powershell
terraform init
terraform apply -var subscription_id=<AZURE_SUBSCRIPTION_ID> -var contact_email=<your email>
```

`monthly_amount` and `start_date` have defaults; see `main.tf`.

## 7. `.env`

`FACTORY_TEMPLATES_REPO` is the GitHub `owner/name` of your template-projects repo, where the
Lessons tab proposes template fixes. From its local clone:
`gh repo view --json nameWithOwner -q .nameWithOwner`.

```dotenv
HCP_ORG=your-org              # section 1
HCP_TOKEN=...                 # section 1: owners team token, factory.ps1 only
HCP_TEAM_TOKEN=...            # section 1: the same token; becomes each target's TF_API_TOKEN secret
HCP_AZURE_VARSET=varset-...   # section 4
AZURE_GH_CLIENT_ID=...        # section 3, written by setup-azure.ps1
AZURE_TENANT_ID=...           # section 3, written by setup-azure.ps1
AZURE_SUBSCRIPTION_ID=...     # section 3, written by setup-azure.ps1
FACTORY_TEMPLATES_REPO=you/template-projects
```

Set `HCP_ORG` last: it is what turns deploys on.

## Check it

Approve a small job against a scaffolded target and watch its card: Building shows `deploy r1 · 1/3`,
then Ready for review shows `preview: up` with Launch. A failed attempt leaves
`.factory-runs/jobs/<id>/deploy-<n>.log` with the GitHub and HCP logs; `factory.ps1 deploy-log <id>`
prints the latest.

Targets scaffolded before HCP deploys existed are brought forward by the devops agent
(`aspire-addon infra adopt`) on their first job, so they need nothing by hand.

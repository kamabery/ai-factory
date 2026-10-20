# Setting up deploys (ephemeral previews)

With deploys on, every job is deployed to a throwaway Azure environment between review and approve:
the approver smoke-tests it, you QA it from Ready for review (**Launch**, **QA login**), and it is
torn down when the job is merged, discarded or reworked, when you press **Tear down**, or after 24
hours by HCP Terraform. Production is never deployed. The rules are in
[FACTORY-CONTRACT.md](FACTORY-CONTRACT.md#deploy); this is the one-time setup.

Deploys are **off** until `.env` has `HCP_ORG`. Everything else keeps working without it.

## 1. Azure: an identity for GitHub Actions

GitHub Actions builds images into the preview's registry and runs the schema step. Create an app
registration for it (no secret - it signs in with OIDC):

```powershell
az ad app create --display-name factory-github-actions --query appId -o tsv      # -> AZURE_GH_CLIENT_ID
az ad sp create --id <appId>
az role assignment create --assignee <appId> --role Contributor --scope /subscriptions/<subscription>
```

`factory.ps1 deploy` adds its federated credential for each target repo
(`repo:<owner>/<name>:environment:preview`) through `az`, under **your** `az login`, so you need
the right to update that app registration.

## 2. Entra External ID: the tenant previews sign in with

Only targets with the auth add-on use it. Terraform cannot create a tenant, so once:

1. Create an **Entra External ID** tenant (Microsoft Entra admin center → External Identities).
   Note its tenant id and subdomain (`<sub>.onmicrosoft.com`).
2. In it, create a **sign-in user flow** (email + password). Note its id.
3. Register an app HCP signs in as, grant it **Application.ReadWrite.All**, **User.ReadWrite.All**
   and **EventListener.ReadWrite.All** (admin consent), and add a federated credential for HCP's
   dynamic credentials (issuer `https://app.terraform.io`, subject
   `organization:<org>:project:*:workspace:*:run_phase:*`).

## 3. HCP Terraform

1. An organization (`HCP_ORG`) and an **organization token** for `factory.ps1` (`HCP_TOKEN`).
2. A **team token** for a team that may run workspaces in the factory's projects
   (`HCP_TEAM_TOKEN`). The factory sets it as each target's `TF_API_TOKEN` secret.
3. A **variable set** (`HCP_AZURE_VARSET`, its id `varset-...`) holding the dynamic credentials,
   which the factory attaches to every target's project:

   | Variable | Category | Value |
   | --- | --- | --- |
   | `TFC_AZURE_PROVIDER_AUTH` | env | `true` |
   | `TFC_AZURE_RUN_CLIENT_ID` | env | an app registration in your Azure tenant with Contributor and User Access Administrator on the subscription, and an HCP federated credential |
   | `ARM_SUBSCRIPTION_ID` · `ARM_TENANT_ID` | env | the subscription and its tenant |
   | `TFC_AZURE_PROVIDER_AUTH_EXTERNAL` | env | `true` (auth add-on only) |
   | `TFC_AZURE_RUN_CLIENT_ID_EXTERNAL` | env | the External tenant app from step 2 |
   | `entra_external_tenant_id` · `entra_external_subdomain` · `entra_external_user_flow_id` | terraform | from step 2 |

## 4. GitHub PAT

Add **Workflows**, **Actions**, **Environments**, **Secrets** and **Variables** (read and write) to
the factory's PAT - see [GITHUB-PAT.md](GITHUB-PAT.md). `gh` must be installed; the factory runs it
with the PAT in its environment only.

## 5. A budget alert

Once, from `factory/infra/budget`:

```powershell
az login
terraform init
terraform apply -var subscription_id=<id> -var contact_email=<you>
```

## 6. `.env`

```dotenv
HCP_ORG=your-org
HCP_TOKEN=...                 # organization token, factory.ps1 only
HCP_TEAM_TOKEN=...            # becomes each target's TF_API_TOKEN secret
HCP_AZURE_VARSET=varset-...
AZURE_GH_CLIENT_ID=...        # step 1
AZURE_TENANT_ID=...
AZURE_SUBSCRIPTION_ID=...
FACTORY_TEMPLATES_REPO=you/template-projects   # where the Lessons tab proposes template fixes
```

## Check it

Approve a small job against a scaffolded target and watch its card: Building shows `deploy r1 · 1/3`,
then Ready for review shows `preview: up` with Launch. A failed attempt leaves
`.factory-runs/jobs/<id>/deploy-<n>.log` with the GitHub and HCP logs; `factory.ps1 deploy-log <id>`
prints the latest.

Targets scaffolded before HCP deploys existed are brought forward by the devops agent
(`aspire-addon infra adopt`) on their first job, so they need nothing by hand.

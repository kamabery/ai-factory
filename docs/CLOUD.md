# A deployed factory (cloud mode on Container Apps)

How to run the factory with no machine of yours in the loop: the dashboard and the runner on Azure Container Apps,
jobs and idea turns on Claude in Microsoft Foundry, the board in Azure SQL. The rules are in
[Cloud mode (deployed)](FACTORY-CONTRACT.md#cloud-mode-deployed). The Terraform is `factory/infra/cloud`, and the
image is `factory/container/Dockerfile`.

**Cost:** about $1–6 a month idle, plus about $0.20 of container time per job and the job's Foundry tokens.
- The dashboard scales to zero when no one has it open (0.5 vCPU, 1 GiB while open). The runner is one Container Apps
  job execution per job (2 vCPU, 4 GiB), nothing between jobs. Both run on the preview platform's environment, which
  has no fixed charge.
- Azure SQL serverless (1 vCore, 0.5 minimum, pausing after an idle hour, 2 GB): about $0.26 an active hour, nothing
  while paused.
- Key Vault and Blob storage cost pennies. The image is public on GHCR, so there is no registry to pay for.
- The resource group has a budget (`monthly_budget`, default $25) that mails `contact_email` at 80% spent and at
  100% forecast.

**Security:**
- One user-assigned managed identity is the factory's only credential. It holds Foundry User on the account, Entra
  admin of its own SQL server (Entra-only), Storage Blob Data Owner on the artifacts container, Key Vault Secrets
  Officer on its vault, and Container Apps Jobs Operator on its runner job. With previews on, it also holds a custom
  role on the platform's Postgres server that can only read, start and stop it (`platform.tf`, spec 24), so the runner
  wakes it before a canary or a deploy and the dashboard's chip puts it to sleep. There are no keys, passwords or
  connection secrets.
- Easy Auth stands in front of the dashboard. Its app registration requires assignment, so only the users you name
  can sign in. Easy Auth signs in to Entra as the managed identity through a federated credential, so the app has
  no client secret either.
- The vault, storage and SQL are public endpoints with Entra-only access, as [STATE.md](STATE.md)'s are. Container
  Apps on Consumption has no fixed outbound address that a firewall could allow, and SQL allows Azure's own services.

## 0. Before you apply

- **Previews are set up** ([DEPLOY.md](DEPLOY.md)): the dashboard and the runner run on the platform's Container Apps
  environment, `cae-factory-platform`.
- **The Foundry account exists** ([FOUNDRY.md](FOUNDRY.md)).
- **The image is on GHCR and public.** Build it from the packed package, never from the repo root, which holds `.env`:

  ```bash
  dotnet pack src/factory/Factory.Cli -c Release -o artifacts/container
  docker build -f factory/container/Dockerfile --build-arg VERSION=<version> -t ghcr.io/<owner>/ai-factory:<version> artifacts/container
  gh auth refresh -s write:packages                 # once: lets gh push packages
  gh auth token | docker login ghcr.io -u <owner> --password-stdin
  docker push ghcr.io/<owner>/ai-factory:<version>
  ```

  Then, under the package's **Package settings**, set its visibility to public (`https://github.com/users/<owner>/packages/container/ai-factory/settings`).

  **A new image forces a canary.** The fingerprint includes the package version, so after a release the first
  deployable job waits in the Canary column while it runs ([DEPLOY.md](DEPLOY.md), "The canary"). A release is
  checked with the read-only `@cloud` check below, not a live cloud job.

## 1. Apply the Terraform

State lives in HCP Terraform (workspace `factory-cloud`, execution mode local). The apply runs on your machine under
your `az login` and `terraform login`, as the previews setup does.

`factory/infra/cloud/local.auto.tfvars` (gitignored):

```hcl
subscription_id              = "<subscription>"
tenant_id                    = "<tenant>"
container_app_environment_id = "<the id of cae-factory-platform>"
foundry_account_id           = "<the Foundry account's id>"
foundry_resource_name        = "<factory/infra/llm's resource_name>"
image                        = "ghcr.io/<owner>/ai-factory:<version>"
github_owner                 = "<GITHUB_OWNER>"
signin_user_object_ids       = ["<your object id in this tenant>"]
settings                     = { HCP_ORG = "<org>", AZURE_GH_CLIENT_ID = "...", FACTORY_PLATFORM_SERVER_ID = "...", ... }  # the previews-env settings, for previews
budget_start_date            = "<this month>-01T00:00:00Z"
contact_email                = "<you>"
```

```bash
cd factory/infra/cloud
terraform init -backend=false; terraform test     # offline, every provider mocked
terraform init                                    # creates the factory-cloud workspace; set it to local execution
terraform plan                                    # read it: nothing outside rg-factory-cloud but the role assignments, the platform role and the app registration
terraform apply
```

## 2. Hand it the secrets

```bash
factory init cloud
```

This copies each secret your machine's store holds into the factory's vault: the GitHub App, its user token and the
HCP tokens. It prints `NAME: set` or `NAME: unchanged`, never a value, and then the dashboard's URL. Your `.env` and
your local board are not touched, because the deployed factory has a board of its own.

## 3. Prove it

1. **Open the dashboard URL.** You sign in with Entra, and the header reads `cloud · foundry`. An account that isn't
   assigned is turned away by Entra.
2. **One idea, end to end.** **New idea** on a target the deployed factory has never built (its job ids start at
   `J-001`, and checkout refuses another board's branch). The approval starts a runner execution. Its logs are in the
   platform workspace:

   ```kusto
   ContainerAppConsoleLogs_CL
   | where ContainerJobName_s == "job-factory-runner" and Log_s startswith "runner "
   | project TimeGenerated, Log_s
   | order by TimeGenerated asc
   ```

   Token use per job, beside the Foundry account's own metrics:

   ```kusto
   ContainerAppConsoleLogs_CL
   | where Log_s has " cost J-"
   | parse Log_s with * " cost " Job " usd=" Usd:real " ms=" Ms:long " in=" In:long " out=" Out:long
   | project TimeGenerated, Job, Usd, In, Out
   ```

3. **The release check.** After every release, the dashboard's read-only Playwright check against the deployed URL:
   the board loads signed in as `cloud · foundry` with its six columns, its API answers, and New idea offers
   **+ New repo**. It writes nothing and spends no tokens. Sign in once to save the session (gitignored):

   ```powershell
   cd factory/tests/dashboard
   npx playwright open --save-storage=.cloud-auth.json https://<the dashboard URL>   # sign in, then close the window
   $env:FACTORY_CLOUD_URL = 'https://<the dashboard URL>'; npx playwright test -c playwright.cloud.config.ts
   ```

4. **No key anywhere.** The containers' environment holds no key, token or password (the Terraform test pins this).
   The secrets are in the vault, read through the identity.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| The container exits with `container refused: no managed identity` | the app or job has no user-assigned identity |
| `az login --identity failed` | `AZURE_CLIENT_ID` names an identity that isn't assigned to the app or job |
| Easy Auth `AADSTS700016` or `invalid_client` | the app registration's federated credential subject isn't the identity's principal id |
| `AADSTS50105` | the user isn't assigned to the app (`signin_user_object_ids`) |
| An approval doesn't start a job | the identity lacks Container Apps Jobs Operator on the job; the dashboard's `runner.log` has `runner job start failed` |
| `board conflict` or `state:` errors at start | `FACTORY_SQL` can't reach the database; check the `AllowAzureServices` firewall rule |
| `Key Vault 403` | the identity isn't Secrets Officer on the vault, or the role hasn't propagated yet (a few minutes) |

## Tearing down

`terraform destroy` in `factory/infra/cloud` removes the resource group, the role assignments and the app
registration. The vault is soft-deleted for 7 days. Foundry, the previews platform and HCP are untouched.

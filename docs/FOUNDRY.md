# Claude in Microsoft Foundry (cloud mode)

In **cloud** mode (`FACTORY_MODE=cloud`) every job's `claude` pass and every dashboard idea turn
runs on Claude in Microsoft Foundry, not on your subscription or an API key. The rules are in
[Run mode and providers](FACTORY-CONTRACT.md#run-mode-and-providers). The code is
`factory/scripts/llm.ps1`, and the Terraform is `factory/infra/llm`.

**Cost:** an idle account costs nothing. Claude bills per token through Azure Marketplace on the
subscription, at Anthropic's list prices. The `cost` event's `costUsd` is `claude`'s own estimate,
and it carries `provider: foundry`, so you can reconcile it against Cost Management.

**Security:**
- Keyless. Local (API key) auth is off on the account, so the only way in is an Entra token.
  Nothing in the factory reads, stores or sends a Foundry key.
- Least privilege. The identities you name get **Foundry User** (formerly Azure AI User) on the
  account only.
- Public endpoint, with Entra auth only. No private endpoint.

## 0. Check before you apply

Run these once per subscription. They are read-only.

```powershell
# Every model must be offered as GlobalStandard in the region, at version 2 (hostedOn: azure).
az cognitiveservices model list --location eastus2 `
  --query "[?contains(['claude-opus-5-5','claude-sonnet-5-5','claude-haiku-5-5'], model.name)].{name:model.name, version:model.version, hostedOn:model.capabilities.hostedOn}" -o table
# Quota must be non-zero for each (the Terraform's default capacities are these limits).
az cognitiveservices usage list -l eastus2 --query "[?contains(name.value,'5-5.Azure')].{name:name.value, limit:limit}" -o table
```

**Marketplace terms.** Every Claude deployment carries a `modelProviderData` attestation
(organization name, two-letter country code, industry), which is how Azure accepts the Marketplace
offer for you; without it the deployment fails with `InvalidModelProviderData`. The subscription
must also be allowed to buy third-party Marketplace offers, which some credit-based subscriptions
(Visual Studio, sponsorship) can't.

## 1. Apply the Terraform

The module keeps its state in HCP Terraform (workspace `factory-llm`) and runs with the factory's
existing OIDC identity (`factory-hcp-terraform`, see [DEPLOY.md](DEPLOY.md)), the same as
`factory/infra/state`.

```powershell
cd factory/infra/llm
terraform init -backend=false; terraform test   # offline, every provider mocked
$env:TF_CLOUD_ORGANIZATION = '<HCP_ORG>'
$env:TF_TOKEN_app_terraform_io = '<HCP_TOKEN from .env>'   # or `terraform login` once per machine
terraform init                       # creates the factory-llm workspace
```

Before the first plan, open the workspace in HCP:
1. **Settings → Variable sets:** apply the Azure variable set (`HCP_AZURE_VARSET`).
2. **Variables:** add
   - `subscription_id`
   - `provider_organization_name`, the legal name the Marketplace attestation names
     (`provider_country_code` defaults to `US`, `provider_industry` to `technology`)
   - `principal_ids`, an HCL list holding the object id that will run cloud mode
     (`az ad signed-in-user show --query id -o tsv`, signed in as that account). A deployed
     factory's managed identity is added here later.

Then:

```powershell
terraform apply
terraform output                     # resource_name, endpoint, deployments
```

`factory-hcp-terraform` already has Contributor and User Access Administrator on the subscription
(DEPLOY.md, section 3), which covers the account, its deployments and the role assignment.

## 2. Point the factory at it

Add to `.env` (a deployed factory sets the same names in its environment, which wins):

```
FACTORY_MODE=cloud
FACTORY_FOUNDRY_RESOURCE=<resource_name>
```

Sign `az` in as the account in `principal_ids`. `claude` and idea turns get their tokens from the
Azure credential chain, which is `az login` on a workstation. A role assignment can take a few
minutes to apply; until then you get `403`.

The model pins default to the deployment names. Override them only if you renamed a deployment:
`ANTHROPIC_DEFAULT_OPUS_MODEL`, `ANTHROPIC_DEFAULT_SONNET_MODEL`, `ANTHROPIC_DEFAULT_HAIKU_MODEL`,
and `FACTORY_IDEA_MODEL` for idea turns.

## 3. Prove it

1. **The dashboard header** reads `cloud · foundry`.
2. **One job.** Approve a small spec. The run log's first `system` line names the models in use, the
   job's `cost` event says `"provider":"foundry"`, and the account's **Metrics** show the calls (`ModelRequests`, `SuccessfulCalls`, `InputTokens`, `OutputTokens`, `FoundryModelEstimatedCost`; `ProcessedPromptTokens` and `GeneratedTokens` stay 0 for Claude).
3. **One idea turn.** **New idea** works with no `ANTHROPIC_API_KEY` in use.
4. **Back to local.** Remove `FACTORY_MODE` (or set it to `local`), and the next job runs on your
   subscription again.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| Job escalated `provider: no FACTORY_FOUNDRY_RESOURCE` | cloud mode without the resource name |
| Job escalated `provider: FACTORY_MODE '<x>' must be local or cloud` | a typo in `FACTORY_MODE` |
| `ChainedTokenCredential authentication failed` in the run log | `az` isn't signed in, or not as a `principal_ids` account |
| `403` from Foundry | the role assignment hasn't propagated yet, or the account isn't in `principal_ids` |
| `404 DeploymentNotFound` | a model pin names a deployment that doesn't exist |
| Deployment `400 InvalidModelProviderData` | the workspace has no `provider_organization_name` (or an old module without `modelProviderData`) |
| Idea turn error `foundry: az account get-access-token failed` | the same as `ChainedTokenCredential`: sign `az` in |

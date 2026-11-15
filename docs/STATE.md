# Factory state in Azure

How to move the factory's state from `.factory-runs/` on one machine into an Azure Blob container,
so several machines (and later a deployed factory) work one board. The rules are in
[State backends](FACTORY-CONTRACT.md#state-backends). The code is `Factory.Store` (`StateSync`, `BlobClient`). The
Terraform is `factory/infra/state`.

**Cost:** under $1/month. Storage at 1 GB LRS is about $0.02. The rest is operations: about $0.45 in
conditional GETs while a dashboard is open, plus pennies for pushes and leases. There is no private
endpoint (about $7.30/month each) and no always-on compute.

**Security:**
- Shared keys and SAS are off. Every call carries an Entra token, and only the principals you name
  get Storage Blob Data Owner, on the container only (Owner because deleting a blob version,
  which `prune` does, needs `deleteBlobVersion/action`; Contributor lacks it).
- TLS 1.2 or later.
- The firewall denies by default; `allow-ip` opens it to the machines that run the factory.
- The bearer token is cached outside `FACTORY_HOME` (`%LOCALAPPDATA%\ai-factory`), so it is never
  synced. It is scoped to storage only.

## 1. Apply the Terraform

The module keeps its own state in HCP Terraform (workspace `factory-state`), not in the account it
creates, and it runs with the factory's existing OIDC identity (`factory-hcp-terraform`, see
`docs/DEPLOY.md`).

```powershell
cd factory/infra/state
$env:TF_CLOUD_ORGANIZATION = '<HCP_ORG>'
$env:TF_TOKEN_app_terraform_io = '<HCP_TOKEN from .env>'   # or `terraform login` once per machine
terraform init                       # creates the factory-state workspace
```

Before the first plan, open the workspace in HCP:
1. **Settings → Variable sets:** apply the Azure variable set (`HCP_AZURE_VARSET`).
2. **Variables:** add
   - `subscription_id`
   - `principal_ids`, an HCL list holding your object id (`az ad signed-in-user show --query id -o tsv`)
   - `ip_rules`, an HCL list holding your public IP

Then:

```powershell
terraform apply
terraform output                     # blob_url, storage_account, resource_group
```

## 2. Point the factory at it

Add to `.env`:

```
FACTORY_STATE=azure
FACTORY_BLOB_URL=<blob_url>
FACTORY_STORAGE_ACCOUNT=<storage_account>
```

`az login` as yourself; the factory gets its token from `az`.

## 3. Migrate the existing state, once

```powershell
factory state-migrate
```

This uploads `board.json`, `events.jsonl` (split by month), and every artifact, eval, lesson, session
and log. It refuses if the container already has a board. Until it has run, every other command
refuses with "run factory state-migrate".

## 4. Adding another machine

Every machine works the same board. The container is the record, and each machine's `.factory-runs/`
is a cache of it. Leases on `locks/` keep machines from colliding: one board write at a time, one
runner, one canary, and one turn per idea session.

**Needs:** PowerShell 7, git and the Azure CLI. Install Claude Code as well if this machine will run
jobs or idea turns. Terraform is not needed.

1. **Clone the repo**, and check out the branch the other machines run.
2. **Copy `.env`** from a machine that already works. It holds the Anthropic key, the GitHub PAT and
   the HCP token, so move it the way you would move a password (a password manager or a USB stick),
   never through git, chat or email. The three `FACTORY_STATE*` / `FACTORY_BLOB_URL` keys in it are
   what point this machine at the container.
3. **Start from an empty cache.** If `.factory-runs/` already exists here (say this machine ran the
   factory with local state), rename it, for example to `.factory-runs.old`. A stale cache with no
   `.sync.json` would push its old files over newer ones.
4. **Sign in to Azure** as an identity that has access:
   ```powershell
   az login --tenant <tenant-id>       # as an identity in principal_ids
   az account set --subscription <subscription-id>
   ```
   Only the object ids in the workspace's `principal_ids` variable are granted. factory-admin is
   already granted. To use another identity (a second person, or a managed identity later), add its
   object id to `principal_ids` in the HCP workspace `factory-state` and apply. Role assignments
   can take a few minutes to take effect.
5. **Open the firewall to this machine:**
   ```powershell
   factory allow-ip
   ```
   This adds the machine's public IP. It records the IP in `%LOCALAPPDATA%\ai-factory\allowed-ip.txt`,
   and on the next run it removes the IP it added last time.
6. **Pull everything:**
   ```powershell
   factory state-pull
   ```
   This fetches the board, every artifact, and the events rebuilt from each month's blob. (Any
   other command pulls the board and artifacts as well, but only `state-pull` and the dashboard
   rebuild `events.jsonl`.)
7. **Check it:** `factory status` lists the same jobs as the other
   machines. `prune` prints the same `<used> of <cap> bytes`.
8. **Start the dashboard** if you want one: `factory serve`. It picks up other
   machines' writes within 5 seconds.

**What to expect once two machines are running:**
- **Runner:** only one drains the Approved queue. The dashboard starts a runner on every machine, but
  a runner that finds `locks/runner` held exits quietly. If the machine running it goes away, the
  lease lapses within 60 s, and the next runner started anywhere takes over.
- **Board writes:** these queue on `locks/board`. "state lock refused" means another machine held
  the lock for longer than the wait, so retry.
- **Idea sessions:** a session can be continued from any machine, but not from two at once (exit
  code 3, "...on another machine").
- **Two machines behind one router** share a public IP. If one later runs `allow-ip` after its IP
  changes, it removes that shared IP and the other machine is locked out (403). Run `allow-ip` on
  the other machine too.

**Removing a machine:** run `az storage account network-rule remove --account-name <storage_account>
--resource-group rg-factory-state --ip-address <its IP>`. If it used an identity of its own, also
take that object id out of `principal_ids` and apply. Its `.factory-runs/` holds nothing the
container lacks once its last command has finished.

## 5. Day to day

- **Your IP changed** (403 `AuthorizationFailure` from storage): `factory allow-ip`.
- **How full is it:** `factory prune`, which prints `<used> of <cap> bytes`. The cap (1 GB, set by
  `FACTORY_STATE_CAP`) rolls off the bulky logs of finished jobs first. It never touches the board,
  events, ideas, evals or lessons.
- **`state push failed: ... 403 OperationNotAllowedOnAutomaticSnapshot`:** the prune tried to delete an
  old version, and your identity has Storage Blob Data Contributor on the container instead of
  Owner. Only Owner can delete versions. Apply `factory/infra/state` (section 1), which assigns
  Owner. Until then each push still uploads, but the container stays over the cap.
- **"state lock refused":** another machine holds `locks/board`. A crashed holder's lease lapses in
  60 s, so wait and retry.
- **A bad board write:** board.json is versioned, and the newest 50 versions are kept. In the portal,
  open the container, then `board.json` → Versions → promote one. Then run any `factory` command.

## Rollback

Set `FACTORY_STATE=local` (or remove it). `.factory-runs/` is a complete cache, so the factory carries
on from the last state this machine saw. Work done on other machines since then stays in the container.

## Moving the board into a database (`sqlite` or `sql`)

Spec 13 adds two backends that keep the board and events in a database, with `board.json` and
`events.jsonl` as exports ([Database](FACTORY-CONTRACT.md#database-sqlite-and-sql)). The code is
`Factory.Data` (the EF Core model and migrations) and `Factory.Store` (`DatabaseStore`, `StateExport`).

**sqlite, one machine.** Set `FACTORY_STATE=sqlite` (the database is `<FACTORY_HOME>/factory.db` unless
`FACTORY_DB` names another file), stop the dashboard and the runner, then move the board in once:

```powershell
factory state-import          # from FACTORY_HOME's board.json and events.jsonl (a local board)
factory state-import -Azure   # from the container at FACTORY_BLOB_URL (an azure board), reading only
```

It prints `imported <i> ideas, <j> jobs, <e> events` and `check: board.json and events.jsonl match the
database`, and rolls back if they don't. Until it has run, every other command refuses with "run factory
state-import". With `-Azure` it also downloads every artifact into `FACTORY_HOME`, so keep
`FACTORY_BLOB_URL` set for that one command.

**sql (Azure SQL).** `FACTORY_STATE=sql` and `FACTORY_SQL=<connection string>`, keyless through Entra
(`Authentication=Active Directory Default`). Provisioning it comes with cloud mode (spec 18); until then
the artifacts stay in each machine's `FACTORY_HOME`, so run one machine per database.

**Rollback.** To `local`: set `FACTORY_STATE=local`. The exports are a whole `board.json` and
`events.jsonl`, so the file backend carries on with everything done on the database. To `azure`:
`state-import -Azure` never writes to the container, so it holds the board as it was at the import; work
done on the database since then is in the exports, not the container.

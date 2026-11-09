# Factory contract

This file is what the factory's moving parts agree on: the dashboard, `serve.ps1`, `runner.ps1`,
the orchestrator skill, the agents, `factory.ps1` and the hooks. When any of them disagrees with
this file, this file wins, and the disagreement is a bug in the other one. The data formats can
change, but only by editing this file first.

## Principle

Agents propose and execute; `factory.ps1` attests and constrains; a human owns intent and the
release. A human touches the factory at exactly two points: **approving a spec**, in an idea
session (or by dragging a Backlog card to Approved), and **accepting the work**
(Ready for review → Done). Everything between those two is automated.

No agent, skill or endpoint writes to `.factory-runs/board.json`. Every state change is a
`factory.ps1` call, and the script refuses the ones this contract does not allow.

## The flow

```
 idea session ───────────── human "yes" per spec ────┐
 grill → brief → spec-writer                         │
 (dashboard, or /factory:idea)                       │
        │ "not yet"                                  ↓
        └────→ Backlog ──human drag──→ Approved ──runner──→ Building ──→ Ready for review ──human drag──→ Done
               (edit spec)             (FIFO queue)        build ←─┐     (flag if the       (merge)
                  │                         ↑              review  │     factory gave up;
                  └──discard                │              deploy ─┘     live preview,
                                            │              approve  app  Launch / Tear down)
                                            │              gate     fix       ├──discard
                                            │              blocked ──retry deploy / rework / discard
                                            └────────── rework <note> ────────┘
```

A **deployable** job (deploys on, not a bootstrap; see [Deploy](#deploy)) leaves Building for Ready
for review only with a live preview of its branch head that passed the smoke check. When the factory
cannot get it there, the job parks in Building as `blocked` instead (see [Blocked](#blocked)).

Done and discarded cards stay until a human clears them ([Purge](#purge), the Done column's **Clear**).

## Board columns and stages

| Column | Stages | Who moves cards out |
| --- | --- | --- |
| Ideas | `idea` records with status `waiting`, `drafting` or `failed`, and open dashboard [idea sessions](#idea-sessions-dashboard) (not jobs) | the idea session, or **Abandon**, on the card or in the idea's opened card |
| Backlog | `backlog` | human (drag to Approved, or discard) |
| Approved | `approved` | runner |
| Building | `build`, `review`, `deploy`, `approve`, `gate`, `blocked` | orchestrator / script; a `blocked` card: human (retry deploy, rework, or discard) |
| Ready for review | `ready` | human (drag to Done, rework, or discard) |
| Done | `done` | nobody (terminal) |
| (hidden) | `discarded` | nobody (terminal) |

In Building, the card shows its sub-stage, its round and the reviewer chips; a `blocked` card also
shows its flag and the **Retry deploy**, **Rework** and **Discard** buttons. Only Backlog,
Approved, Ready for review and Done accept drops.

An Approved card says what it is waiting on (`data-wait`), using the rules `next` applies (see
[Canary](#canary)). The first rule that matches wins:
1. `waiting: J-NNN is building` while a job is in Building, because jobs run one at a time.
2. `waiting: deploy canary` while `canary.json` says `running`. The runner runs the canary itself,
   so every approved job waits for it.
3. `held: deploy canary failed` for a deployable job (deploys on and not a bootstrap) after a failed
   canary.
4. `after J-NNN` when an older approved job that is not held is ahead of it.
5. `next` otherwise.

The dashboard works this out from `board.json`, `canary.json` and `deploys` in
`GET /api/ideate/config`. A pass recorded for an older fingerprint still reads `next`; the runner
then starts a canary, and the card switches to rule 2.

## Targets

Every idea names a **target**: a GitHub repository, written `owner/name`
(`^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$`). The human types it next to the idea; nothing defaults it. The
factory itself (this repo's `factory/`, `.claude/`, `.factory-runs/` and dashboard) never moves; only
the code it builds goes to the target. Local paths are not targets.

**GitHub access.** `factory.ps1` reads `GITHUB_TOKEN` and `GITHUB_OWNER` from `.env` at the repo root
(`$env:FACTORY_ENV_FILE` overrides the file). The token is a fine-grained PAT with Contents,
Pull requests and Administration (to create repos) read/write, on **All repositories**: a PAT limited
to selected repos can create a repo but not push to it (see [GITHUB-PAT.md](GITHUB-PAT.md)). It is read into a local variable and
never set in the process environment, written to a remote URL or `.git/config`, or logged: git gets
it per command as `-c http.extraHeader=...`. `GITHUB_OWNER` is the owner the dashboard suggests.
`GITHUB_API_URL` (default `https://api.github.com`) and `FACTORY_GIT_BASE` (the clone URL prefix,
default `https://github.com`, giving `<base>/<owner>/<name>.git`) in the same file point tests and
the simulator at a stub.

- An existing repo with a `main` branch: jobs branch from its `main` and land in it by pull request.
  The idea goes straight to the spec-writer in the same session.
- A **new** target (the repo does not exist, or exists with no `main`) needs **scaffold options**,
  chosen by the human in the idea form. `new-idea` then creates a **bootstrap job** instead of
  starting the spec-writer: its spec, generated from `factory/templates/bootstrap.md`, scaffolds
  the project from the `template-projects` templates: the builder runs `/aspire-starter:boot-strap-app`
  with every answer supplied (no interview, no pack install, no graphify phase), so the add-ons go
  through the `aspire-addon` CLI and the gate adds `dotnet build .aspire/apphost.cs` and
  `aspire-addon infra check`. The idea waits (`waiting`) until that job is
  merged; the merge releases it (`drafting`), and the human resumes it with `/factory:idea I-NNN`,
  so the spec-writer specs features against real code.
  The first `checkout` creates the repo on GitHub (**private**; under the token's user when the
  owner is that user, else in the named organization) if it is missing, and pushes `main` with an
  empty initial commit if it has none. The control plane does this, never an agent.
- Anything else (not `owner/name`, scaffold options for an existing repo, a new target without them,
  a GitHub error such as a bad token) is refused by `new-idea`. `approve` refuses a Repo that is not
  `owner/name`.

Scaffold options (`-Scaffold <json>` on `new-idea`, `idea.scaffold` on the board):

```json
{ "name": "todo", "resource": "Todo", "db": "sqlite", "ui": "react", "events": false }
```

| Key | Values |
| --- | --- |
| `name` | `^[a-z][a-z0-9]*$`; the templates' `-n` (gives `todo.slnx`, `src/todo.api`, `tests/todo.api.tests`) |
| `resource` | `^[A-Z][A-Za-z0-9]*$`; the templates' `--resource` |
| `db` | `sqlite`, `sqlserver`, `postgres` |
| `ui` | `react`, `blazor`, `blazor-maui`, `none` |
| `events` | `true` / `false`; the event store follows `db` (fisher, polecat, marten) |
| `auth` | optional, `true` / `false`: sign-in (`aspire-addon add auth`: Keycloak locally, Entra External ID in Azure); refused with `ui: none` |

The template workspace is `$env:FACTORY_TEMPLATES`, else `<parent of this repo>\template-projects`.
Agents may read it and never edit it. It is also a plugin (`aspire-starter`, its own
`.claude-plugin/plugin.json`), loaded into every headless pass; see [Runner](#runner).

**Work directories.** Code changes and testing happen in a local temp clone per job,
`<work>\jobs\<id>`, where `<work>` is `$env:FACTORY_WORK`, else `<system temp>\ai-factory`.
`checkout` clones the target there and checks out `factory/<id>`. The clone is deleted when the job
moves to `done` or `discarded`. It survives a rework, and if it is gone `checkout` clones again from
the pushed branch. One job builds at a time across all targets. Idea sessions read a shallow clone of
an existing target (`clone-target`) in `<work>\sessions\<S>` (dashboard) or
`<work>\cli\<owner>-<name>` (`idea.ps1`), deleted when the session closes. A session with a
[base](#stacked-jobs) reads `factory/<base>` instead of `main`.

The spec carries the target and the gate, and the human can edit both in Backlog:

```
## Target
- Repo: kamabery/todo-app

## Gate
- build: `dotnet build`
- test: `dotnet test`
```

Gate lines are ``- <name>: `<command>` ``, where `<name>` is lower-case letters, digits or `-`. The
commands run with `pwsh -Command` in the job's work directory. An unquoted `-name=value` token with a
dot in it is quoted first, because pwsh would split it at the dot (J-022's `-var-file=envs/ephemeral.tfvars`). The human approves them along with the spec, and
no agent can edit `jobs/<id>/spec.md`.

The spec's `## Scope` `- In:` line is followed by one sub-bullet per path the builder may touch,
each starting with a backticked glob, repo-relative, with `/` separators, then optional prose:

```
## Scope
- In:
  - `.aspire/apphost.cs` (Keycloak resource, `Auth__*` on api)
  - `.aspire/keycloak/**`
  - `IaC/auth.*.tf`
  - `src/ui/react/package-lock.json` (generated)
- Out: any change to the API's controllers
```

`*` matches within one path segment and `**` across segments. These globs are what the
[scope check](#build-checks) compares the diff with, so the spec-writer lists generated outputs
(lockfiles, `contracts/api.openapi.json`, `routeTree.gen.ts`) where the change will regenerate them.
A spec with no backticked sub-bullets under `- In:` (written before this rule) is not scope-checked,
and `set-stage review` appends a `note` event saying so.

A dependency install in a gate is guarded, so the gate does not reinstall on every run. For npm,
that means `npm ci` runs only when `node_modules/.package-lock.json` (which npm writes at the end
of an install) is missing or older than `package-lock.json`:

```
- ui: `Set-Location src/ui/react; if (-not (Test-Path node_modules/.package-lock.json) -or (Get-Item package-lock.json).LastWriteTime -gt (Get-Item node_modules/.package-lock.json).LastWriteTime) { npm ci }; npm run build`
```

## Run mode and providers

The factory runs in one of two **modes**, and the mode alone decides the **provider**, the endpoint
every `claude` pass and every dashboard idea turn talks to. Nothing chooses a provider per job.

| `FACTORY_MODE` | Provider | Credentials |
| --- | --- | --- |
| `local` (the default) | `subscription`: the `claude` CLI's own login | the human's claude.ai login; idea turns: `ANTHROPIC_API_KEY` in `.env` (interim, see below) |
| `cloud` | `foundry`: Claude on Microsoft Foundry, resource `FACTORY_FOUNDRY_RESOURCE` | Microsoft Entra ID only (the Azure credential chain: `az login` on a machine, a managed identity when deployed); no key anywhere |

- `FACTORY_MODE` is read from the process environment first, then `.env`, and is `local` when
  neither sets it, so every launch on a workstation is local. A deployed factory sets
  `FACTORY_MODE=cloud` in its environment. Any other value is refused by every script that reads it.
- A job's builder, reviewers and approver are subagents of its one `claude` pass, so they share the
  provider. The runner resolves it when it takes a job; a `provider` field left on a job by an older
  factory is ignored.
- **local.** For the `claude` child the runner sets empty `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`,
  `ANTHROPIC_BASE_URL`, `CLAUDE_CODE_USE_FOUNDRY`, `ANTHROPIC_FOUNDRY_RESOURCE`,
  `ANTHROPIC_FOUNDRY_BASE_URL`, `ANTHROPIC_FOUNDRY_API_KEY` and `ANTHROPIC_FOUNDRY_AUTH_TOKEN`, and
  restores them afterwards. Any of them inherited from the human's environment (a Windows user
  variable, say) would take precedence over the CLI's login and bill something other than the
  subscription.
- **cloud.** The runner sets, for the `claude` child only, `CLAUDE_CODE_USE_FOUNDRY=1`,
  `ANTHROPIC_FOUNDRY_RESOURCE=<FACTORY_FOUNDRY_RESOURCE>`, and `ANTHROPIC_DEFAULT_OPUS_MODEL`,
  `ANTHROPIC_DEFAULT_SONNET_MODEL` and `ANTHROPIC_DEFAULT_HAIKU_MODEL` (the deployment names: the
  environment or `.env` value, else `claude-opus-5-5`, `claude-sonnet-5-5` and `claude-haiku-5-5`).
  It sets empty `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_BASE_URL`,
  `ANTHROPIC_FOUNDRY_BASE_URL`, `ANTHROPIC_FOUNDRY_API_KEY` and `ANTHROPIC_FOUNDRY_AUTH_TOKEN`, so
  `claude` authenticates through Entra ID and nothing else. **Cloud mode never runs on a
  subscription**: there is no setting that makes it, and a claude.ai login present on the machine is
  not used, because `CLAUDE_CODE_USE_FOUNDRY` takes precedence over it.
- With no `FACTORY_FOUNDRY_RESOURCE`, a cloud job escalates (`provider: no FACTORY_FOUNDRY_RESOURCE`)
  without running `claude`, and an idea turn fails with the same message.
- The identity needs **Foundry User** (formerly Azure AI User) on the Foundry account, and nothing
  wider. The account, its deployments and the role are `factory/infra/llm` ([FOUNDRY.md](FOUNDRY.md)).
- `costUsd` is `claude`'s own estimate from list prices whatever the provider; the `cost` event
  carries the resolved `provider` (`subscription` or `foundry`), so a Foundry bill can be reconciled
  against it.
- Idea turns follow the same rule; see [Idea sessions](#idea-sessions-dashboard). Running local idea
  turns on the subscription (through `claude -p`, with no API key) is for the .NET port; until then a
  local idea turn uses `ANTHROPIC_API_KEY`.

## Ideas

An idea is worked out in an idea session. There are two doors, and they follow the same steps:
the dashboard's **New idea** panel, which talks to the Claude API (see
[Idea sessions](#idea-sessions-dashboard)), and the CLI fallback, an interactive Claude Code session
`/factory:idea [<target> | I-NNN]` (`factory/scripts/idea.ps1` starts one with the plugin loaded).
An idea is not a job. The session:

1. Takes the target, and for a new target the scaffold options.
2. Grills the human (the plugin's `grill` skill) until nothing in the idea is assumed, and has them
   confirm the resulting **brief**, which follows `factory/templates/brief.md`.
3. Runs `new-idea` and writes the brief to `ideas/I-NNN/brief.md`. For a new target it shows the
   bootstrap spec, approves it on the human's yes, and stops: the idea waits for the bootstrap merge.
4. Runs the spec-writer, which turns the brief into 1..N specs (N ≤ 5), each small and buildable on
   its own. It shows each spec; the human approves it, asks for changes (made to `spec-<k>.md`
   before it reaches the board), defers it, or rejects it.
5. For each approved spec: `add-spec`, then `approve` (it lands in Approved and the session starts
   the runner). A deferred spec gets `add-spec` only and waits in Backlog. A rejected spec never
   reaches the board. Then `idea-done`, or `idea-done -Failed` if every spec was rejected.

`drafting` means "open for a session". `/factory:idea I-NNN` resumes a `drafting` idea from its
`brief.md`, after a bootstrap merge or an interrupted session.

```
board.ideas[] = { "id": "I-001", "text": "...", "target": "kamabery/todo-app",
                  "scaffold": null, "base": null, "status": "waiting|drafting|done|failed|abandoned",
                  "jobs": ["J-004", "J-005"], "createdAt": "..." }
```

- `factory.ps1 new-idea <text> -Target <path> [-Scaffold <json>] [-Base <J-NNN>]` validates the target and options
  (see [Targets](#targets)), allocates `I-NNN` and writes `ideas/I-NNN/idea.md` (the text, then a
  last line `Target: <path>`). For an existing repo it sets status `drafting`. For a new target it
  sets status `waiting`, creates the bootstrap job in `backlog` (`kind: "bootstrap"`, listed in the
  idea's `jobs`) and prints `I-NNN J-NNN`.
- The spec-writer reads `idea.md` and `brief.md` (the brief wins where they differ) and writes
  `ideas/I-NNN/spec-1.md .. spec-N.md`, following `factory/templates/spec.md`.
  It copies the idea's target into each spec's `## Target` verbatim and never chooses one.
- `factory.ps1 add-spec <ideaId> <file>` allocates `J-NNN`, copies the file to `jobs/J-NNN/spec.md`,
  takes the title from the file's `# Spec:` heading, and creates the job in `backlog`. `add-spec`,
  `edit-spec` and `approve` set `job.target` from the spec's `Repo:` line.
  `-Base` (an existing target only) records the job the idea builds on as `base`; see
  [Stacked jobs](#stacked-jobs).
- `factory.ps1 idea-done <ideaId> [-Failed -Reason <text>]` closes the idea. Ideas in `done`
  are no longer shown; ideas in `failed` stay in Ideas with the reason until a `purge`.
- `factory.ps1 idea-abandon <ideaId>` sets an idea in `waiting`, `drafting` or `failed` to
  `abandoned`: the human is not doing it. It is hidden like `done`, its files stay until a
  `purge`, and jobs it already created are left where they are. It appends an `idea` event
  `abandoned`.

## Idea sessions (dashboard)

The dashboard's idea session runs the steps of [Ideas](#ideas) against the Claude API, not the
`claude` CLI. Build, review, approve and gate still run on the CLI through the runner. A session is
a conversation stored under `sessions/<S-NNN>/`. It is not on the board, and **the model never
changes board state**: every board write in a session is a human click that `serve.ps1` turns into
one `factory.ps1` call.

**The credential.** It follows the [run mode](#run-mode-and-providers). `FACTORY_IDEA_MODEL` in
`.env` overrides the model in both modes (default `claude-opus-5-5`, which in cloud mode is the
Foundry deployment name), and `ANTHROPIC_BASE_URL` the endpoint in both modes (tests point it at a stub).
- **local:** the API key is `ANTHROPIC_API_KEY` in `.env` at the repo root (gitignored;
  `.env.example` names the keys), sent as `x-api-key`. The key is read from the file into a local
  variable by `idea-turn.ps1` and never set in the process environment, so a `claude` started by
  the runner keeps using the subscription.
- **cloud:** the endpoint is `https://<FACTORY_FOUNDRY_RESOURCE>.services.ai.azure.com/anthropic`,
  and every request carries `Authorization: Bearer <token>`, an Entra token for
  `https://ai.azure.com` from `az account get-access-token`, refreshed when it has under 10 minutes
  left. No key is read or sent. Foundry has no server-side fallback, so the request carries no
  `fallbacks` and no `server-side-fallback` beta.

`serve.ps1` only checks that the mode's credential is configured (`ANTHROPIC_API_KEY` locally,
`FACTORY_FOUNDRY_RESOURCE` in cloud mode). Without it, the dashboard shows the `idea.ps1` hint
instead of **New idea**.

**Turns.** Each assistant turn is a detached `factory/scripts/idea-turn.ps1 -Session <S> -StateDir
<dir> -EnvFile <path>` process. It holds `sessions/<S>/turn.lock` (exclusive create) for the whole
turn. While the lock is held, every session write endpoint answers 409. The turn streams the
Messages API (`stream: true`) and appends what arrives to `stream.jsonl`, which is truncated when a
turn starts. It loops over the model's tools until the model stops or calls a UI tool. Then it
appends the finished messages to `chat.json`, adds the turn's cost to `session.json`, and releases
the lock. The system prompt is assembled at turn time from `factory/plugin/prompts/idea-api.md`,
the `grill` skill, the spec-writer agent and `factory/templates/brief.md` and `spec.md`.

**Tools.**

| Tool | Kind | Effect |
| --- | --- | --- |
| `read_file`, `list_dir`, `grep` | server, read-only | fenced to the session's clone of the target (`checkout`, when the target exists) and `factory/templates`; any other path is refused |
| `submit_specs { specs: [{ k, markdown }] }` (1..5) · `revise_spec { k, markdown }` | server | write `ideas/<I>/spec-<k>.md`; refused until the brief is confirmed (the session has an idea) |
| `ask_questions { questions }` (AskUserQuestion's shape) · `propose_scaffold { repo?, name, resource, db, ui, events }` (`repo`, a repo name, is required while the session has no target) · `submit_brief { markdown }` | UI | end the turn; `session.json.pending` holds the call until the human answers |

```
sessions/<S>/session.json = { "id": "S-001", "target": "owner/name" | null, "text": "...", "newTarget": false,
  "checkout": "<work>\\sessions\\S-001" | null,
  "status": "running|idle|error|closed", "error": null, "model": "claude-opus-5-5", "costUsd": 0.12,
  "scaffold": null, "idea": "I-004", "bootstrap": null,
  "pending": { "id": "toolu_...", "name": "ask_questions", "input": { } } | null,
  "pendingResults": [ ],
  "specs": [ { "k": 1, "title": "...", "decision": null | "approved" | "backlog" | "rejected", "job": "J-007" } ],
  "createdAt": "..." }
stream.jsonl lines: { "type": "text", "text" } · { "type": "tool", "name", "summary" } · { "type": "end" } · { "type": "error", "message" }
human.jsonl lines: { "ts", "kind": "idea", "text", "target", "newTarget" }
                 · { "ts", "kind": "answer", "text", "asked": { "name", "input" } } · { "ts", "kind": "message", "text" }
```

`human.jsonl` is the human's own words in the session, verbatim and in order, so an idea can be
tried again as the factory improves. `serve.ps1` appends a line for the idea text, for every reply
that answers a pending UI tool (an `answer`, with that tool call as `asked`: the questions, the
scaffold proposal, or the brief being corrected) and for free text with nothing pending (a
`message`). Retries, clicks, scaffold form values and direct spec edits are not words and are not
recorded there. Nothing deletes a session directory, so `purge` keeps it.

`pendingResults` holds the results of server tools that ran in the same assistant message as a UI
tool. They are sent with the human's answer in one user message.

**Session flow.** The human picks a target from their GitHub repos (`list-repos`), optionally a
**base** (`main`, or an unmerged job on that target; see [Stacked jobs](#stacked-jobs)), and describes
the idea. `check-target` classifies the target, and the panel creates the session. Or the human picks
**Add new**: the session starts with no target (`target: null`, `newTarget: true`), and the model
grills first, then names the repo in `propose_scaffold`. Confirming that form runs `check-target`
on `<GITHUB_OWNER>/<repo>`, which must be `new`, and sets `target`; `scaffold` is stored without
`repo`. For an existing target, `serve.ps1` runs `clone-target` (with `-Base` when the session has
one, recorded as `base` in `session.json`) into `<work>\sessions\<S>` and records it as `checkout`; **Write specs** clones again (the bootstrap
has merged), and closing the session deletes the clone.

1. The model grills (`ask_questions`) and, for a new target, calls `propose_scaffold`. The human
   confirms or edits the form.
2. The model calls `submit_brief`. The panel shows it with **Confirm** and **Correct**. Correct
   sends the human's words back as the tool result.
3. Confirm runs `new-idea` (with `-Scaffold` for a new target, `-Base` for a session with a base)
   and writes `ideas/<I>/brief.md`.
   - **Existing target:** the turn continues to the specs.
   - **New target:** the panel shows the bootstrap job's spec with Approve (the usual move
     endpoint) or leave it in Backlog. After the bootstrap merge, the idea is `drafting` and
     **Write specs** continues the session.
4. The model calls `submit_specs`. Each spec card offers **Approve** (`add-spec` + `approve`, then
   the runner starts), **Change** (ask the model, which calls `revise_spec`, or edit the markdown
   directly), **Not yet** (`add-spec`), or **Reject**. Once every spec is decided, the panel closes
   the session: `idea-done`, or `idea-done -Failed -Reason "every spec rejected in session"`.

## Job transitions

`by` is `human`, `runner`, `orchestrator` or `gate`.

| From | To | Allowed when | `flag.reason` |
| --- | --- | --- | --- |
| backlog | approved | human; `spec.md` has a valid `## Target` and at least one `## Gate` command. Sets `approvedAt` and `target` | |
| backlog | discarded | human | |
| approved | build | runner; no job is in `build`, `review`, `approve` or `gate`, and this is the oldest `approvedAt` | |
| build | review | `round-<n>/build.md` exists and the [build checks](#build-checks) pass | |
| build | ready / blocked | `set-stage review` found a file outside the spec's Scope: In (`ready` if not deployable, else `blocked`) | `out-of-scope` |
| build | ready / blocked | `set-stage review`: the job has `baseMoved` and build.md's `## Spec against base` says `Spec holds: no` | `spec-stale` |
| review | build | all 4 verdicts in, at least one `CHANGES`, round < 2; round += 1, reviews reset to `pending` | |
| review | ready | at least one `CHANGES` at round ≥ 2, and the job is not deployable | `max-rounds` |
| review | blocked | at least one `CHANGES` at round ≥ 2, and the job is deployable | `max-rounds` |
| review | deploy | all 4 verdicts are `PASS`, and the job is deployable (see [Deploy](#deploy)) | |
| review | approve | all 4 verdicts are `PASS`, and deploys are off or the job is a bootstrap (its `deploy.yml` reaches `main`, where GitHub looks for workflows, only when it merges); a `note` event says which | |
| deploy | approve | `factory.ps1 deploy` on a bootstrap job (one that reached `deploy` before this rule): skips the preview, attempts aside, with a `note` | |
| deploy | approve | `factory.ps1 deploy` saw the preview applied for the branch head and its smoke check pass, and only then; it makes the move | |
| deploy | build | `factory.ps1 deploy-fix <id> -File <diagnosis>`: the last attempt failed, the devops agent judged the change at fault, and `deployFixes` < 2. Writes `jobs/<id>/deploy-fix-<n>.md` for the new round n, round += 1, `deployFixes` += 1, reviews reset to `pending`, `deploy.attempts` back to 0 | |
| deploy | blocked | `deploy-fix` when `deployFixes` is already 2 | `deploy-fix-rounds` |
| deploy | blocked | the fifth failed `deploy` of the round; the preview is left up for diagnosis | `deploy-failed` |
| deploy | blocked | `factory.ps1 block <id> -Detail <line>`: the last attempt failed and the devops agent judged the environment or the factory at fault (a credential, a quota, a factory bug), not the change | `environment` |
| deploy | blocked | GitHub refused a call because the GitHub PAT lacks a permission; the attempt is not counted, and the detail names the permission | `github-permission` |
| blocked | approved | human `redeploy <id>`, when the job was blocked from `deploy` (the human fixed the cause). Sets `resume: "deploy"` and `approvedAt`, and `deploy.attempts` back to 0; round and `deployFixes` are unchanged | |
| blocked | approved | human `rework <id> <note>`, as from `ready` | |
| blocked | discarded | human `discard <id>`, as from `ready` | |
| ready | approved | human `redeploy <id>`, for a job that reached `ready` from `deploy` before `blocked` existed (flag `github-permission`, `deploy-failed` or `escalated`). Sets `resume: "deploy"` and `approvedAt`, and `deploy.attempts` back to 0 | |
| approved | deploy | runner; the job has `resume: "deploy"` and is next in the queue (as for `build`). Clears `resume`; `/factory run` then starts at its deploy step | |
| approved | build / review / deploy / approve | runner; the job was queued by `resume`, its `resume` names this stage, and it is next in the queue. Clears `resume` and `resumeSession`, keeps `roundStart` and the reviews; the runner then continues the stopped claude session (see [Runner](#runner)) | |
| approve | gate | `decision.md` first line is `Decision: APPROVE` | |
| approve | ready | `Decision: ESCALATE` | `escalated` |
| gate | ready | every gate command exited 0 (no flag), or any one did not. Run by the orchestrator, or by the runner when claude ended with the job in `gate` | `gate-failed` |
| build / review / deploy | blocked | the job is deployable, and the orchestrator gives up or the runner's `claude` process ends with the job still here | `escalated` |
| build / review / deploy / approve | ready | the job is not deployable (or is in `approve`, so its preview is up), and the orchestrator gives up or the runner's `claude` process ends with the job still here | `escalated` |
| build / review / deploy / approve | blocked / ready | the runner's `claude` stopped on a usage limit (see [Runner](#runner)): `set-stage <id> ready -By runner -Reason usage-limit -Detail <result text> -Session <session id>`. `blocked` or `ready` as for `escalated`. The flag also holds `stage` (where it stopped) and `session` | `usage-limit` |
| ready / blocked | approved | human `resume <id>`, when the flag is `usage-limit`. Sets `resume` to the flag's `stage`, `resumeSession` to its `session`, and `approvedAt`; round, reviews, `deploy.attempts` and `deployFixes` are unchanged | |
| ready | done | human `merge`, or `sync-prs` after the human merged the PR on GitHub; see [Gate and merge](#gate-and-merge). Merges the PR (or records the GitHub merge) and deletes the work directory. A bootstrap job's merge moves its idea `waiting` → `drafting` and prints `released <ideaId>` | |
| ready | ready | human `merge` refused: nothing to merge, uncommitted changes in the work directory, PR not mergeable / red gate | `merge-blocked` / `gate-failed` |
| ready | approved | human `rework <id> <note>`; writes `jobs/<id>/rework-<n>.md`, round += 1, `deployFixes` back to 0, sets `approvedAt`. A `Scope +:` line in the note widens the spec (see [Build checks](#build-checks)) | |
| ready | discarded | human; closes the PR, deletes branch `factory/<id>` on GitHub and the work directory | |

`done` and `discarded` are terminal. `flag` is cleared on every move out of `ready` or `blocked`.

`merge` (and `sync-prs`) and `discard` also tear down the job's preview when it has one that is
not `down`: they start `factory.ps1 destroy <id>` detached and do not wait for it. A failed destroy
is never a failed move: it is recorded on the job, shown on its card, and retried by
`factory.ps1 sweep` (see [Deploy](#deploy)). HCP's auto-destroy timer fires once, so it is no
backstop for a run that errors. `rework`
leaves the preview up: the next round's `deploy` applies over it on the same workspace. A destroy
there raced that apply, which then rebuilt from nothing behind it (J-022).

### Blocked

`blocked` is where a deployable job waits for a human when the factory cannot get it deployed. It
stays in the Building column, so Ready for review means "there is a live preview to look at". It is
not counted as building: the runner takes the next Approved job while a job is blocked, and the
blocked job keeps its work directory, its PR (the runner publishes it) and its preview, which HCP's
auto-destroy timer removes after 24 hours. Its flag is one of `max-rounds`, `deploy-fix-rounds`,
`deploy-failed`, `environment`, `github-permission`, `out-of-scope`, `spec-stale`, `escalated` or
`usage-limit`.

The human has four ways out:

- **Resume** (`resume`), for `usage-limit` only, once the limit has reset: the job goes back to the
  stage it stopped in and the runner continues the same claude session, so nothing is re-run.
- **Retry deploy** (`redeploy`), once the cause is fixed. Only for a job blocked from `deploy`; one
  blocked from `build` or `review` never had a passing round to deploy.
- **Rework** with a note: a new round, as from Ready for review.
- **Discard**: closes the PR, deletes the branch and the work directory, and tears the preview down.

A factory fault (a race in a script, a bad credential) is fixed in a human session and then
retried with **Retry deploy**, never sent to the builder as a rework note. The builder can change
only the target, so a note like J-022's round-3 "Fix raced condition" makes it "fix" the target
(`IaC/adopt.tf`, three rounds and $6 to undo). The rework dialog says so above the note.

### Build checks

`set-stage <id> review` runs three deterministic checks after the builder and the devops author pass,
in this order. The first that fails moves the job (see [Job transitions](#job-transitions)), and none
of them is an agent's judgment.

**1. Scope.** `git diff --name-only factory/base...HEAD` in the work directory (the job's own
changes, with the base's merged work excluded) is compared with the spec's Scope: In globs
([Targets](#targets)). Any path that matches none of them escalates the job with flag `out-of-scope`,
detail `<n> paths outside Scope: In: <first path>, ...`, and `why` listing every path. The devops
agent's `IaC/` and workflow edits are checked like the builder's: J-022's stray `IaC/adopt.tf` was
in `IaC/`.

A rework note widens the scope with one line, `Scope +: <glob>, <glob>`. `rework` appends each glob
as an In sub-bullet to `jobs/<id>/spec.md`, followed by `(rework <n>)`, and appends a `note` event
naming them. This is the only way `spec.md` changes after approval. The human's word is the
authority, `factory.ps1` makes the write, and the spec keeps a record of every widening. From
`blocked` with `out-of-scope`, the human reworks with `Scope +:` (keep the paths) or with a note
asking the builder to remove them.

**2. Base moved.** When a job merges (`merge`, or `sync-prs` recording a GitHub merge), every other
job of the same idea that is not `done` or `discarded` gets
`baseMoved: { "by": "<merged id>", "sha": "<merge commit>", "at": "..." }` and a `note` event. A
Backlog card shows it ("base moved: J-021 merged"), so the human can edit the spec before approving.
In the job's next build round, build.md must contain:

```
## Spec against base
Spec holds: yes | no
<why, and if no: the Scope + and AC changes the spec needs>
```

`set-stage review` exits 2 (the job does not move) if the section or its `Spec holds:` line is
missing. On `yes` it clears `baseMoved`. On `no` it escalates with flag `spec-stale`, the section as
`why`, and keeps `baseMoved`. The human then reworks with the proposed `Scope +:` and AC wording, or
discards. A rework clears `baseMoved`, since the human has now decided against the new main.

**3. Template provenance** (rounds after 1). A rework or deploy-fix round's fix to code that came
from a template means the next scaffold carries the same bug (J-022's `identifier_uris` and
`auth.ts` fixes came from the auth add-on, and its lessons said "None"). Each file changed this
round (`git diff --name-only <roundStart>..HEAD`) is **template-sourced** when the commit that added
it (`git log --diff-filter=A --format=%H -- <path>`, oldest) is either:
- reachable from the target's bootstrap merge commit, which is the `merged.sha` of the
  `kind: bootstrap` job with the same target (later jobs usually come from other ideas), from the
  board or else from `evals/<id>.json`, or
- a commit whose subject starts with `scaffold:` or `addon:`. The builder and the bootstrap spec
  make every `dotnet new` and `aspire-addon add` its own commit with that prefix.

For each template-sourced path, build.md's `## Template gaps` section must name the path, either
as a gap (Symptom / Cause / Fix applied here / Belongs in, as in deploy.md) or as
`<path>: job-specific, <why>`. A path that is not named makes `set-stage review` exit 2 and print
the missing paths, so the orchestrator sends the builder back once to add them. If the paths are
still missing after that, it gives up. `factory.ps1 provenance <id>` prints the template-sourced
paths of the round, so the builder can write the section before it hands on.

"Changed this round" means the round's own commits: `git log --no-merges --name-only
<roundStart>..HEAD --not factory/base`, so work from the base branch that the builder merged in is
not counted. With no bootstrap merge on record for the target (a repo the factory did not
bootstrap), only `scaffold:` and `addon:` commits count, and `provenance` says so on stderr. A job
with no `roundStart` (built before it existed) is not checked, and a `note` event says so.

### Evaluations

An evaluation judges how well the factory handled one job: the spec's fit to the brief, the build
against the acceptance criteria, whether the reviewers caught what mattered, and the cost and time.
It is written by a human session (Claude Code reviewing the run with the human), never by a
`/factory` agent, and it changes no stage. Its markdown follows `factory/templates/eval.md`, and
its first line must be exactly `Rating: good`, `Rating: mixed` or `Rating: poor`.

`factory.ps1 eval <id> -File <path>` takes a job in `ready`, `done` or `discarded`. It copies the
file to `evals/<id>.md` and writes `evals/<id>.json`, a snapshot of the board facts at that moment:

```json
{ "job": "J-011", "idea": "I-006", "title": "...", "kind": "feature", "target": "D:\\...",
  "rating": "mixed", "stage": "ready", "round": 2, "flag": "max-rounds" | null,
  "reviews": { "security": "PASS", "code": "CHANGES", "ux": "PASS", "ui": "PASS" },
  "decision": "APPROVE" | null, "gate": "green" | "red" | null, "merged": "<sha>" | null,
  "costUsd": 4.48, "durationMs": 914000, "evaluatedAt": "...",
  "models": { "claude-opus-5-5": { "costUsd": 4.1, "outputTokens": 90000 } },
  "agents": [ { "agent": "builder", "round": 1, "ms": 600000 } ],
  "gateMs": 43000 }
```

`models` sums `modelUsage` over the `"type":"result"` lines of every `run-<n>.jsonl`. `agents` pairs
each `agent-start` with the next `agent-stop` of the same agent and round in `events.jsonl`, in
order of first start. `gateMs` sums each gate run, from the `stage` event into `gate` to its `gate`
event. The dashboard shows these facts as a **Gate, cost and time** grid above the markdown.

Running `eval` again for the same job replaces both files, e.g. after the merge. It appends an
`eval` event whose `detail` is the rating. `purge` never touches `evals/`, so an evaluation outlives
its job's card, and `purge` keeps a finished job until it has one.

**Human feedback** is the human's own verdict after a hands-on check of the built product, kept
apart from the session's evaluation. `factory.ps1 feedback <id> -File <path>` takes any job that has
an `evals/<id>.json`, even one purged from the board. The file's first line must be a `Rating:` line,
as for `eval`; the rest is free text. It copies the file to `evals/<id>.feedback.md`, sets
`"human": { "rating": "poor", "at": "..." }` in `evals/<id>.json` (absent until then), and appends a
`feedback` event whose `detail` is the rating. Running it again replaces both. A re-`eval` keeps
`human`.

The round cap applies to automated loops only. A reworked job gets one build and one review pass
at its new round. If the reviewers still return `CHANGES`, it comes back to Ready for review
flagged `max-rounds`.

Approval of a spec, the approver's `APPROVE`, a green gate and a completed merge are four separate
facts. The board records each one on its own. Only a human drag to Done merges.

### Purge

`factory.ps1 purge` clears finished work off the board. It removes every job in `done` or
`discarded` that has an evaluation (`evals/<id>.json`), and every idea in `done`, `failed` or `abandoned` none of whose jobs is left. For each it deletes
`jobs/<id>/` or `ideas/<id>/`, and for a job also a work directory still left at `<work>\jobs\<id>`
(one that a locked file kept `merge` or `discard` from deleting). It appends one `note` event (`purged J-001, ...`) and
prints the removed ids, one per line. Ideas in `waiting` or `drafting` and jobs in any other stage
are untouched. A finished job with no evaluation stays on the board, with its `jobs/<id>/`, until it
is evaluated: purging it would delete the run logs an evaluation is written from. `events.jsonl`
keeps its history, and `board.seq` stops a purged id being reused.

### Lessons

When a job goes bad, what it taught is kept so the **templates** can absorb it - the
`template-projects` add-ons, the `aspire-addon` CLI and the `aspire-starter` skills - and the next
scaffold does not need the same hand fix.

`factory.ps1 lesson <id> [-File <path>]` writes `lessons/<id>.md` and `lessons/<id>.json`. Without
`-File` it compiles them from the job, deterministically: the flag (reason, detail and `why`), and the
`## Template gaps` section of every `round-*/deploy.md` that is not empty or "none". With nothing to
say - no flag and no gaps - it writes nothing and prints `no lessons`. It reads the
`## Template gaps` section of every `round-*/build.md` the same way, so a builder's fix to
template-sourced code (see [Build checks](#build-checks)) becomes a lesson; a `job-specific` line is
not a gap. With `-File` (a human
evaluation session) it appends that file's text under `## From the evaluation`. Either way it appends a
`lesson` event whose `detail` is the number of template gaps.

The runner calls `factory.ps1 lesson <id>` after every pass that leaves the job in `ready` or
`blocked`, beside `publish`; a failure is logged and changes nothing.

```json
{ "job": "J-012", "title": "...", "target": "kamabery/todo-app", "flag": "deploy-failed" | null,
  "templateGaps": 2, "status": "open" | "proposed" | "applied", "session": "S-004" | null, "at": "..." }
```

`factory.ps1 lesson-mark <id> <open|proposed|applied> [-Detail <session id>]` changes `status` (and
`session`). The dashboard's **Lessons** tab lists every lesson, newest first, opens its markdown, and
**Propose template fix** starts an idea session (`POST /api/sessions`) on the repo `.env` names as
`FACTORY_TEMPLATES_REPO` with the selected lessons as its text, then marks each `proposed` with that
session. A human marks one `applied` once the fix has merged. `purge` never touches `lessons/`.

## Files

```
.factory-runs/                 gitignored; FACTORY_HOME overrides its location
  board.json
  events.jsonl
  runner.lock                  held while runner.ps1 is draining the queue
  serve.lock                   held while serve.ps1 runs: its PID, then its URL. A second serve.ps1 on
                               the same state refuses to start and names that URL; a lock whose PID is
                               no longer running is taken over, as runner.lock's is
  canary.lock                  held while a canary runs
  canary.json                  the last canary's result and fingerprint (see Canary)
  canary-<n>.log               canary run n's log
  headless-settings.json       written by runner.ps1 for each claude -p run (see Runner)
  sessions/<S-NNN>/            a dashboard idea session (see Idea sessions)
    session.json  chat.json  stream.jsonl  human.jsonl  turn.lock
  ideas/<id>/
    idea.md                    the idea's one-line title, then `Target: <path>`
    brief.md                   the grilled brief, in the human's words (factory/templates/brief.md)
    spec-<k>.md                spec-writer output, one per proposed job
  jobs/<id>/
    spec.md                    copied from the idea, then edited by the human in Backlog
    rework-<n>.md              the human's rework note for round n
    deploy-fix-<n>.md          the devops agent's diagnosis of a failed deploy, the builder's input for round n
    round-<n>/
      build.md
      review-security.md  review-code.md  review-ux.md  review-ui.md
      deploy.md                the devops agent's report: what it deployed, fixes after review, template gaps
    decision.md
    gate.log
    gate-check.log  gate-check.json  gate-check.lock   gate-check's last output, its cache by (sha, name), and its lock
    deploy-<a>.log             attempt a's GitHub job log and HCP run log, written when it failed
    run-<n>.jsonl              the claude -p stream-json output of each runner pass, written live
  evals/                       kept by purge (see Evaluations)
    <id>.md  <id>.json         the evaluation, and the board facts snapshotted when it was written
    <id>.feedback.md           the human's feedback on it, if any
  lessons/                     kept by purge (see Lessons)
    <id>.md  <id>.json         what a job that went bad taught, for the templates

<work>\                       $env:FACTORY_WORK, else <system temp>\ai-factory
  jobs\<id>\                   the job's clone of its target, on factory/<id>; deleted at done / discarded
  canary\                      the canary's scaffold, deleted when the run ends
  sessions\<S>\                a dashboard idea session's shallow clone of its target
  cli\<owner>-<name>\          idea.ps1's shallow clone of its target

factory/plugin/                the agents, the /factory, /factory:idea and grill skills and the Subagent
                               hooks, as a Claude Code plugin loaded with --plugin-dir, so they work in
                               any job's work directory
```

Each review's first line must be exactly `Verdict: PASS` or `Verdict: CHANGES`. The decision's
first line must be exactly `Decision: APPROVE` or `Decision: ESCALATE`. `factory.ps1` parses
those lines and nothing else; if one is missing or malformed, the script treats it as no verdict.

## `board.json`

```json
{
  "version": 2,
  "seq": { "I": 1, "J": 1 },
  "ideas": [
    { "id": "I-001", "text": "Let me find books quickly", "status": "done", "jobs": ["J-001"], "createdAt": "2026-10-12T13:55:00Z" }
  ],
  "jobs": [
    {
      "id": "J-001",
      "idea": "I-001",
      "title": "Book search filter",
      "stage": "review",
      "round": 1,
      "branch": "factory/J-001",
      "base": null,
      "kind": "feature",
      "target": "kamabery/todo-app",
      "pr": { "number": 12, "url": "https://github.com/kamabery/todo-app/pull/12" },
      "reviews": { "security": "running", "code": "PASS", "ux": "pending", "ui": "CHANGES" },
      "decision": null,
      "gate": null,
      "flag": null,
      "merged": null,
      "approvedAt": "2026-10-12T14:00:00Z",
      "costUsd": 1.84,
      "durationMs": 412000,
      "createdAt": "2026-10-12T13:56:10Z",
      "updatedAt": "2026-10-12T14:06:12Z",
      "history": [ { "ts": "2026-10-12T14:00:00Z", "from": "backlog", "to": "approved", "by": "human" } ]
    }
  ]
}
```

- **Key order is fixed.** A factory with no `board.json` yet starts from `version`, `ideas`, `jobs`,
  in that order; keys added later follow in the order they were added, and every write keeps the
  file's order. `status -Json` prints the board in that order (an empty board as `version`, `ideas`,
  `jobs`), so two runs print the same text.
- `reviews.*` is one of `pending | running | PASS | CHANGES`, and it resets to `pending` whenever
  a new round starts.
- `roundStart` is the branch head when the current round's build began, recorded by `checkout`.
  The provenance check diffs from it.
- `baseMoved` is `null` (or absent) or `{ "by": "J-NNN", "sha": "...", "at": "..." }`: a job from
  the same idea merged after this one was created (see [Build checks](#build-checks)).
- `gate` is `null` until the gate runs, then `{ "build": 0, "test": 0, "ui": 0, "sha": "<branch head>", "at": "..." }`
  (process exit codes).
- `flag` is `null` or `{ "reason": "<code from the table>", "detail": "<one line>" }`. Only a
  `ready` or `blocked` job carries one. A `usage-limit` flag also has `"stage"` (the Building stage
  claude stopped in) and `"session"` (claude's `session_id`).
- `deployFixes` is the number of rounds started by `deploy-fix` since the last rework, 0..2; absent
  means 0. When the approver escalates, `read-decision` copies the decision's
  `## Escalation reason` section into the flag: its first line is `detail`, and the whole section
  is `why` (markdown). An empty or `N/A` section leaves `detail` as `approver escalated; see
  decision.md` and no `why`. The job modal shows what the reason code means, then `detail`, then `why`.
- `merged` is `null` until a merge, then `{ "sha": "<merge commit>", "repo": "<owner/name>", "pr": "<PR url>", "added": ["<repo-relative path>", ...], "modified": [...] }`,
  from `git diff --name-status --no-renames <sha>^1 <sha>` in the work directory after fetching the merge.
- `pr` is `null` (or absent) until `publish` opens the job's pull request.
- `resume` is absent (or `null`) except on an Approved job queued by `redeploy`, where it is `"deploy"`:
  the runner moves that job to `deploy` instead of `build`, and clears it. A job queued by `resume`
  has the stage it stopped in (`build`, `review`, `deploy` or `approve`) and `resumeSession`, the
  claude session to continue; the runner moves it to that stage and clears both.
- `deploy` is `null` (or absent) until the first `deploy`, then
  `{ "env": "j001", "workspace": "<repo>-j001", "workspaceId": "ws-...", "status": "applying|up|failed|destroying|down", "attempts": 1, "sha": "<deployed head>", "run": "<GitHub run url>", "urls": { "frontend": "...", "api": "...", "health": "..." }, "appliedAt": "...", "destroyAt": "...", "destroyStartedAt": "...", "destroyError": null, "destroyFailures": 0 }`. The last three exist once a destroy has started; a successful destroy clears the error and the count.
  `urls.frontend` is null for an API-only target. `destroyAt` is HCP's auto-destroy time; after it
  the dashboard treats the preview as gone whatever `status` says. `destroy` and `qa-login` use
  `workspaceId`, so they need `HCP_TOKEN` but not `HCP_ORG`: a dashboard can tear down and log in to
  previews while its own deploys are off.
- `base` is `null` (or absent), or the id of the unmerged job this one is stacked on, copied from its
  idea by `add-spec`; see [Stacked jobs](#stacked-jobs).
- `kind` is `feature` (from a spec-writer spec) or `bootstrap` (generated by `new-idea`); absent means `feature`.
- A job has no `provider`: the run mode decides it (see [Run mode and providers](#run-mode-and-providers)).
  One written by an older factory is ignored.
- `costUsd` and `durationMs` are summed over every runner pass on the job.
- Ids are `I-NNN` and `J-NNN`, allocated by the script, one above the larger of `seq` and the highest
  id on the board. `seq` records the highest id ever allocated, so a `purge` never frees an id. The simulator uses the same ids in its own
  state dir (`.factory-runs-sim/`), against the GitHub stub (`factory/tests/fixtures/github-stub.ps1`)
  serving bare repos under `.factory-runs-sim/github`.

## `events.jsonl`

One JSON object per line, append-only. The dashboard builds its timeline and its "agent running
now" pulses from this file.

```json
{"ts":"2026-10-12T13:55:00Z","idea":"I-001","kind":"idea","detail":"drafting"}
{"ts":"2026-10-12T14:02:10Z","job":"J-001","kind":"agent-start","agent":"security-reviewer","agentId":"a2f5009","transcript":"C:\\Users\\me\\.claude\\projects\\x\\0089\\subagents\\agent-a2f5009.jsonl","round":1}
{"ts":"2026-10-12T14:04:55Z","job":"J-001","kind":"agent-stop","agent":"security-reviewer","round":1}
{"ts":"2026-10-12T14:05:00Z","job":"J-001","kind":"stage","from":"review","to":"approve","by":"orchestrator"}
{"ts":"2026-10-12T14:07:30Z","job":"J-001","kind":"gate","detail":"build=0 test=0 ui=0 sha=3f2a1c9"}
{"ts":"2026-10-12T14:08:00Z","job":"J-001","kind":"cost","costUsd":1.84,"durationMs":412000,"provider":"subscription"}
```

`kind` is one of `idea | stage | agent-start | agent-stop | gate | merge | cost | note | eval | feedback | deploy | destroy | lesson | canary`.
A `canary` event has no `job`; its `detail` is `pass` or `failed: <first error line>`.
A `deploy` event's `detail` is `up <frontend or api url>` or `failed (attempt <a>): <first error line>`; a
`destroy` event's is `down` or `failed: <message>`. Spec-writer
agent events carry `"idea"` instead of `"job"`. `agent` is the plain agent name (the hook strips the
plugin's `factory:` prefix). `agent-start` carries `agentId` and `transcript`, the subagent's live
Claude Code transcript, which is derived from the hook's `transcript_path` and `agent_id`.

## State backends

`$env:FACTORY_STATE` is `local` (the default) or `azure`. With `local`, everything above is the whole
story. With `azure`, an Azure Blob container is the system of record and `FACTORY_HOME` is its
write-through cache: agents, `serve.ps1`'s `/state/*` and every reader keep working on local files,
and `factory.ps1` and `serve.ps1` keep the two in step. All of this lives in `factory/scripts/state.ps1`.
Provisioning and the runbook are in `docs/STATE.md`.

**Settings** (environment, else `.env`): `FACTORY_STATE`; `FACTORY_BLOB_URL`, the container's URL
(`https://<account>.blob.core.windows.net/factory`); `FACTORY_STATE_CAP`, the container's size
cap in bytes, default `1073741824` (1 GB); `FACTORY_STORAGE_ACCOUNT` and `FACTORY_STATE_RG`
(default `rg-factory-state`), used only by `allow-ip`. Environment only: `FACTORY_STATE_POLL_SECONDS`
(default 5, see Dashboard below).

Overrides, for tests: `FACTORY_STATE_TOKEN` (the bearer token), `FACTORY_LOCAL_DIR` (in place of
`%LOCALAPPDATA%\ai-factory`), `FACTORY_STATE_LOCK_SECONDS` (the board lease's wait),
`FACTORY_STATE_RENEW_SECONDS` (the keepers' interval), `FACTORY_IP_URL` (allow-ip's public-IP
lookup, default `https://api.ipify.org`), and a `{home}` in `FACTORY_BLOB_URL`, which becomes a hash of
`FACTORY_HOME` so each test's home gets its own container. `FACTORY_TEST_STATE=azure` runs the Pester
suites on a blob stub (`factory/tests/fixtures/blob-stub.ps1`).

**Auth** is Entra only; the account has shared keys disabled. The bearer token comes from
`$env:FACTORY_STATE_TOKEN` (tests), else the managed identity (`IDENTITY_ENDPOINT` and
`IDENTITY_HEADER`, in Azure), else `az account get-access-token --resource https://storage.azure.com`
(through `FACTORY_AZ_CMD` when set). An `az` token is cached until five minutes before it expires in
`%LOCALAPPDATA%\ai-factory\storage-token.json`, never under `FACTORY_HOME`.

**Layout.** Blob names are paths relative to `FACTORY_HOME`, with `/` separators, except:

| Blob | Local | Rule |
| --- | --- | --- |
| `board.json` | `board.json` | block blob, versioned; written only with `If-Match` on the ETag it was read at, under the board lease |
| `events/<yyyy-MM>.jsonl` | `events.jsonl` | append blob per UTC month; an event is appended locally, then as one block. A pull rebuilds `events.jsonl` from every month, oldest first |
| `locks/<name>` | — | empty blobs that only carry leases: `board`, `runner`, `canary`, `turn-<S-NNN>` |
| `jobs/<id>/transcripts/agent-<agentId>.jsonl` | same | a subagent's transcript, copied in at its `agent-stop` |

Never synced: `*.tmp`, `*.lock`, `headless-settings.json`, `.local/` (a machine's own files, such
as a token cache that `FACTORY_LOCAL_DIR` put under the home), `.sync.json` (the cache's manifest:
each synced path's length, mtime and ETag).

**Locks.** In `azure`, each lock is also a 60 s lease on its `locks/` blob, taken alongside the
local one:
- `board`: taken where the board mutex is, released and re-taken with it, and renewed before every
  board write. A lease that cannot be taken within 30 s, or renewed, refuses the command
  (`state lock refused`).
- `runner`, `canary`, `turn-<S>`: renewed every 20 s by a background thread while the holder runs.
  A lease held elsewhere means another machine holds the lock: `runner.ps1` exits 0, `canary` and
  the turn refuse as they do for a local lock.
A crashed holder's lease lapses within 60 s.

**Sync.**
- **Board:** every `factory.ps1` command reads it with `If-None-Match`; when it changed, it refreshes
  `board.json` and pulls the rest of the container.
- **Push:** every `factory.ps1` command ends, still under the board lock, by pushing every local file
  whose length or mtime differs from `.sync.json`, and by deleting the blob of every path in
  `.sync.json` that is gone locally (`purge`, `discard`). A path the cap rolled off is marked pruned
  in `.sync.json` and is never pushed again.
- **Pull:** pulls download every blob whose ETag differs from `.sync.json`, except a path changed
  locally since its last sync. Local changes win, last writer wins, and only `board.json` is
  ETag-checked. A pull never deletes local files.
- **Dashboard:** when the dashboard polls `/state/board.json` or `/state/events.jsonl`, `serve.ps1`
  refreshes that file with `If-None-Match`, at most once per `FACTORY_STATE_POLL_SECONDS` (the poll runs
  every 1.5 s; one conditional GET per file per 5 s costs about $0.45 a month while a dashboard is
  open). It pulls a job's folder when the job's files are listed. A transcript whose path is not on
  this machine is served from `jobs/<id>/transcripts/`.
- **Idea turns:** `idea-turn.ps1` pulls `sessions/<S>/` before the turn and pushes at its end.

**Cap.** After a push that uploaded anything, `factory.ps1` lists the container with versions. While
the total exceeds `FACTORY_STATE_CAP`, it deletes in this order:
1. `board.json` versions older than the newest 50.
2. For each job that is `done`, `discarded`, or no longer on the board (purged), oldest first (by
   `updatedAt`, or by the blob's last-modified time for a purged one): its `run-*.jsonl`, `transcripts/`,
   `deploy-*.log` and `gate.log`.
3. Those jobs' whole folders, in the same order.
4. Sessions, oldest `session.json` first, whose `turn` lease is free.

It never deletes the current `board.json`, `events/`, `ideas/`, `evals/`, `lessons/`, or a job that is
not `done` or `discarded`. It records one `note` event per pass: `pruned <n> blobs, <bytes> freed`.
The cap applies only to the container. Local files are kept.

## `factory.ps1` commands

State resolves from `$env:FACTORY_HOME`, else `<main checkout>/.factory-runs` (found through
`git rev-parse --git-common-dir` from this repo; the runner sets both for agents in a work directory). Writes to `board.json` are
atomic (a temp file, then a rename; the rename is retried for up to 2 s while a reader such as
`serve.ps1` holds the file). Every command holds the board lock, a mutex named for the state
directory, for its whole run, except around a long step (a gate command, a deploy or canary poll,
or `checkout`'s clone and fetch of the job's work directory, which nothing else touches while the
job is in an active stage), after which it re-takes the lock and re-reads the board. A command that cannot take the lock within
30 s (`FACTORY_BOARD_LOCK_SECONDS`) is refused (`board lock refused: another factory.ps1 has held
the board for over <n> s`) rather than run unlocked, where its save could overwrite another
command's. A refused command exits non-zero with a one-line reason on
stderr and leaves the board unchanged.

| Command | Used by |
| --- | --- |
| `new-idea <text> -Target <path> [-Scaffold <json>] [-Base <J-NNN>]` · `add-spec <ideaId> <file>` · `idea-done <ideaId> [-Failed -Reason]` · `idea-abandon <ideaId>` | `/factory:idea`, serve.ps1 (idea sessions, **Abandon**) |
| `check-target <owner/name>` (prints `new` or `existing` from the GitHub API, or refuses as `new-idea` would) · `clone-target <owner/name> <dir> [-Base <J-NNN>]` (replaces `<dir>` with a `--depth 1` clone of `main`, or of `factory/<base>`) | serve.ps1 (idea sessions), idea.ps1 |
| `list-repos` (prints a JSON array of the token user's own repos, `owner/name`, newest push first, at most 100) | serve.ps1 (New idea) |
| `edit-spec <id> -File <path>` (backlog only) | serve.ps1 |
| `approve <id>` | `/factory:idea` (on the human's yes), serve.ps1 (human drag) |
| `rework <id> <note>` · `discard <id>` (both from `ready` or `blocked`) · `merge <id>` · `purge` | serve.ps1 (human drags, the blocked card's buttons, and **Clear**) |
| `gate-check <id> [<name>]` (runs the spec's gate commands, or the one named, in the job's work directory exactly as `gate` does, and records nothing on the board; prints the output and exits with the first non-zero code; see [Gate and merge](#gate-and-merge)) · `provenance <id>` (prints this round's template-sourced paths) | the builder, the reviewers, the approver, `/factory run` |
| `sync-prs` (moves each `ready` job whose PR was merged on GitHub to `done`; prints their ids) | serve.ps1 (PR sync) |
| `next` (prints the next runnable approved id, or nothing; nothing at all while any job is flagged `usage-limit`, since the limit holds for every job until the human resumes; a deployable job is runnable only after a canary pass for the current fingerprint, and `next -NeedsCanary` prints `canary` when that is all that holds it back) · `checkout <id>` (creates the GitHub repo if needed, then clones or reuses `<work>\jobs\<id>` on `factory/<id>`, fetches, and points `factory/base` at the base branch; prints the work directory) · `publish <id>` (`ready` or `blocked`: pushes `factory/<id>` and opens the job's PR if it has none; prints the PR url) · `set-cost <id> <usd> <ms>` | runner.ps1 |
| `set-stage <id> <to> [-By] [-Reason] [-Detail] [-Session]` · `set-review <id> <role> <state>` · `read-reviews <id>` · `read-decision <id>` · `gate <id>` | `/factory run` |
| `deploy <id>` (deploy only) · `deploy-log <id>` (prints the last failed attempt's log) · `deploy-fix <id> -File <diagnosis>` · `block <id> -Detail <line>` (both deploy only, after a failed attempt) | `/factory run`, the devops agent (`deploy-log` also the approver) |
| `redeploy <id>` (`blocked` from `deploy`, or `ready` from `deploy` before `blocked` existed: back to Approved with `resume: deploy` and a fresh attempt budget once the cause is fixed; the runner resumes it at deploy) | serve.ps1 (**Retry deploy**), a human |
| `resume <id>` (`ready` or `blocked` with flag `usage-limit`: back to Approved, to continue its claude session in the stage it stopped in) | serve.ps1 (**Resume**), a human |
| `canary [-Force]` (see [Canary](#canary)) | runner.ps1 (before a deployable job), serve.ps1 (**Run canary**), a human |
| `destroy <id>` (any stage; a no-op without a preview) · `qa-login <id>` (prints the preview's QA user and password) | serve.ps1 (**Tear down**, **QA login**), and `merge`/`discard` |
| `sweep` · `canary-destroy` (see [Deploy](#deploy) and [Canary](#canary)) | serve.ps1 at startup and runner.ps1 when it drains; `canary` and `sweep` start `canary-destroy` |
| `event <json \| -Job -Idea -Kind -Agent>` | hook-event.ps1 |
| `eval <id> -File <path>` | a human session evaluating a finished job (see [Evaluations](#evaluations)) |
| `feedback <id> -File <path>` | serve.ps1 (the human's feedback on an evaluation) |
| `lesson <id> [-File <path>]` · `lesson-mark <id> <open\|proposed\|applied> [-Detail <session>]` | runner.ps1, a human evaluation session, serve.ps1 (see [Lessons](#lessons)) |
| `status [-Json]` | anyone |
| `state-migrate` (`azure` only: uploads everything in `FACTORY_HOME` that is not already in the container, then writes `.sync.json`; refuses when the container already has a `board.json` newer than the local one) · `state-pull [<prefix>]` (pulls the container, or one prefix such as `jobs/J-019/`) · `prune` (runs the cap now and prints `<used> of <cap> bytes`) | a human; serve.ps1 (`state-pull jobs/<id>/` when a job is opened) |
| `allow-ip` (`azure` only: adds this machine's public IP to `FACTORY_STORAGE_ACCOUNT`'s firewall through `az storage account network-rule add`, and removes the IP it added last time, recorded in `%LOCALAPPDATA%\ai-factory\allowed-ip.txt`) | a human, after the public IP changes |

`read-reviews` and `read-decision` exit 2 when a verdict line is missing or malformed; the job
does not move. `set-stage` only performs `approved → build`, `build → review` and
`Building → ready -Reason escalated`, which it turns into `→ blocked` for a deployable job outside
`approve` and `gate`; every other transition has its own command.

Overrides, for tests: `$env:FACTORY_GATE_CMDS` (JSON `[{ "name", "cmd" }]`; wins over the spec's
`## Gate`, so tests can use `exit 0` / `exit 1`), `$env:FACTORY_WORK` (the work root),
`$env:FACTORY_ENV_FILE` (the `.env` holding the GitHub settings) and `$env:FACTORY_REPO` (this
repo's main checkout). For [Deploy](#deploy): `$env:FACTORY_GH_CMD` and `$env:FACTORY_AZ_CMD` (a
script run with `pwsh -File` in place of `gh` / `az`), `$env:FACTORY_DEPLOY_POLL_SECONDS` (default
15) and `$env:FACTORY_DEPLOY_TIMEOUT_MINUTES` (default 60).

## Agents

| Agent | Model | Reads | Writes |
| --- | --- | --- | --- |
| spec-writer | opus | idea.md, brief.md, the target repo | ideas/<id>/spec-1.md .. spec-N.md (N ≤ 5) |
| builder | opus | spec.md, previous round's reviews, rework note, deploy-fix diagnosis | source on the job branch, round-n/build.md |
| security-reviewer | opus | spec.md, the diff, build.md | round-n/review-security.md |
| code-reviewer | sonnet | same | round-n/review-code.md |
| ux-reviewer | sonnet | same | round-n/review-ux.md |
| ui-reviewer | sonnet | same + the target's docs/DESIGN.md, if any | round-n/review-ui.md |
| devops | sonnet | spec.md, the diff, build.md, `deploy-<a>.log`; loads the template workspace's `aspire-starter:deploy-app` skill | `IaC/` and `.github/workflows/` on the job branch, round-n/deploy.md, round-n/deploy-fix.md (the diagnosis it hands `deploy-fix`) |
| approver | opus | spec.md, every build.md, review and deploy.md in the job; smoke-tests the preview | decision.md |

Every agent works in the job's work directory and follows the target's `CLAUDE.md`, if it has one.
The devops agent runs twice per round: after the builder (author: `aspire-addon infra adopt` /
`infra check`, so the reviewers see the infrastructure the round needs), and in `deploy` (operate:
`factory.ps1 deploy`, and on a failure `deploy-log` and a diagnosis; see [Deploy](#deploy)). It edits
the target's `IaC/` and workflows, never this repo's, and never the app's source: a fault in the app
goes back to the builder.
Reviewers inspect the diff (`git diff main...HEAD`) and re-run the spec's Gate commands themselves,
through `factory.ps1 gate-check <id> [<name>]`, never by typing the command. They treat `build.md`'s
claims as claims, not as evidence. The builder runs `gate-check` before it hands on. A gate command
that the builder could not run, or that `gate-check` reports red, means the builder must not hand
on: J-022's round-10 builder shipped a `terraform test` run it could not execute, and the gate
failed it.

## Gate and merge

`factory.ps1 gate <id>` runs the gate commands in the job's work directory on `factory/<id>`, in
order (cloning it again first if it is gone). It writes the output to `gate.log` and records the exit
codes and the branch head `sha`. The commands come from `$env:FACTORY_GATE_CMDS` if set, else from
the spec's `## Gate`.

**`factory.ps1 gate-check <id> [<name>]`** is the same run for agents, so the agents check exactly
what the gate will check. The headless allowlist's prefix rules cannot match a compound gate command
(`Set-Location IaC; terraform init ...; terraform test ...`), and the `factory.ps1` path rule
already lets agents call it. It differs from `gate` in four ways:
- It takes a job in `build`, `review`, `deploy` or `approve`.
- It never changes `gate`, the stage or the flag, and appends no event.
- It holds `jobs/<id>/gate-check.lock` while it runs, so the four parallel reviewers do not run
  `dotnet build` in one work directory at once. A second caller waits for the lock, up to
  `$env:FACTORY_GATE_CHECK_WAIT_MINUTES` (default 20).
- It caches each command's exit code and output in `gate-check.json` by (head sha, name). A repeat
  call on the same head prints the cached result, marked `(cached at <sha>)`, and does not re-run.
  A dirty work tree is never cached.

`factory.ps1 publish <id>` (job in `ready`) pushes `factory/<id>` from the work directory to the
target and opens a pull request into `main` titled `<id>: <title>`, unless the job already has an
open one; it records `pr` and appends a `note` event. The runner calls it whenever a pass leaves the
job in `ready`, so the human can read the diff on GitHub. A rework pushes to the same branch, which
updates the same PR.

**Merged on GitHub.** The human may accept the job by merging its PR on GitHub instead of dragging to
Done. That merge is the acceptance, so the factory records it rather than refusing it:
`factory.ps1 merge <id>` first asks GitHub for the job's PR, and if it is merged, does step 4 below
with the PR's `merge_commit_sha` and merges nothing. It appends a `note` event "PR #N merged on
GitHub", adding "at <head>, not the gated <sha>" when the merged head is not the gated one. That is
noted, not refused, because the code is already in `main`. `factory.ps1 sync-prs` does the same for
every `ready` job with a PR that GitHub reports merged, and prints each one's id. The dashboard runs it
(see [Watchdog](#runner)).

`factory.ps1 merge <id>` otherwise runs in the job's work directory, after `git fetch origin`:

0. It refuses a branch with no commits beyond `origin/main` (`merge-blocked`, "nothing to merge"),
   and a work directory with uncommitted changes (`merge-blocked`, naming the paths), since they
   would not be in the PR.
1. If no all-zero gate is recorded for the current head sha of `factory/<id>`, it runs `gate`
   first. A red gate leaves the job in `ready` with flag `gate-failed`. A human can overrule a
   reviewer flag by dragging to Done, but cannot skip the gate.
2. It runs `publish` (push, and the PR if there is none yet).
3. It merges the PR through the API with `merge_method: merge` and `sha` set to the gated head, so
   GitHub refuses it if the branch moved. A refusal (a conflict, branch protection, a moved head)
   leaves the job in `ready` with flag `merge-blocked` and GitHub's message as the detail.
4. On success it deletes the branch `factory/<id>` on GitHub, fetches the merge commit to fill
   `merged`, moves the job to `done`, appends a `merge` event, and deletes the work directory. A
   work directory it cannot delete (a locked file) is a `note` event, never a failed merge; `purge`
   tries again.

`factory.ps1 discard <id>` closes the job's open PR, deletes `factory/<id>` on GitHub if it was
pushed, and deletes the work directory.

### Stacked jobs

A job can build on another job's work before that work is merged: a fix found while reviewing a
`ready` job, or a follow-up to it. The idea session's **base** names that job, and the idea's jobs
are **stacked** on it (`base` on the idea and on each job).

- **A base** is a job on the same target that is not `done` or `discarded` and has a PR. `new-idea
  -Base` and `clone-target -Base` refuse anything else (`base refused: <why>`). A new target cannot
  have one.
- **`checkout`** starts a new `factory/<id>` from `origin/factory/<base>` while the base is unmerged,
  and from `origin/main` once the base is `done` (its commits are in `main` by then).
- **`publish`** opens the PR into `factory/<base>` while the base is unmerged, so the PR shows only
  this job's diff; otherwise into `main`.
- **`merge`** refuses while the base is not `done` (`merge refused: J-021 is stacked on J-020; merge
  J-020 first`). If the PR still points at `factory/<base>`, it is retargeted to `main` first.
- **Merging the base** (step 4 of `merge`, and a merge recorded from GitHub) retargets the open PR
  of every job stacked on it to `main` before deleting `factory/<base>`, because GitHub closes a PR
  whose base branch is deleted. Merges are merge commits, so the retargeted PR still shows only its
  own diff.
- **`discard`** refuses a job that has live stacked jobs (not `done` or `discarded`): discard those
  first, or merge the base.
- A rework of the base does not move a stacked job. It stays on the head it branched from, and a
  conflict shows up as `merge-blocked` when it merges.

## Deploy

Between review and approve, every job's branch is deployed to an **ephemeral preview** in Azure:
environment `j<NNN>` (the job id, e.g. `j012`), resource group `rg-<project>-j012`, HCP Terraform
workspace `<repo name>-j012` in the HCP project named after the repo. The approver smoke-tests it,
and the human QAs it from Ready for review. Production is never deployed: the target's
`IaC/guards.tf` refuses it and the workflow accepts only `dev`, `staging` and `j<NNN>`.

**On or off.** Deploys are on when `.env` has `HCP_ORG`. Off, `read-reviews` moves an all-PASS job
straight to `approve` and appends a `note` event "deploy skipped: no HCP_ORG in .env". On, every job
deploys; a target without HCP-ready infrastructure gets it from the devops agent's author pass. A
job is **deployable** when deploys are on and it is not a bootstrap.

**Settings** (`.env`, read like `GITHUB_TOKEN`: into a local variable, never into the environment of
anything but the one child process that needs it, never logged):

| Key | What |
| --- | --- |
| `HCP_ORG` | the HCP Terraform organization |
| `HCP_TOKEN` | a team token (the owners team) for `factory.ps1`: projects, workspaces, runs, state outputs. Not an organization token: those can't queue runs, and `destroy` queues one |
| `HCP_TEAM_TOKEN` | a team token scoped to the factory's projects, set as each target's `TF_API_TOKEN` GitHub secret |
| `HCP_AZURE_VARSET` | id of the variable set holding Azure (and Entra External) dynamic credentials, attached to every project |
| `AZURE_GH_CLIENT_ID` · `AZURE_TENANT_ID` · `AZURE_SUBSCRIPTION_ID` | the app registration GitHub Actions signs in as (OIDC), for image builds and schema steps |
| `HCP_API_URL` | default `https://app.terraform.io`; tests point it at a stub |

**`factory.ps1 deploy <id>`** (job in `deploy`), in order; any step's failure is a failed attempt:

0. A bootstrap job never deploys: GitHub dispatches only workflows on the default branch, and the
   bootstrap is what puts `deploy.yml` there. `deploy` moves it to `approve` with a `note`, whatever
   its attempts, and does nothing else, except that a `deploy` record left by earlier attempts is
   set to `status: down` (no preview), so the card stops showing a stale failure. Its first feature
   job is the first preview.
1. Refuses when `deploy.attempts` is already 5. Increments `attempts`, sets `status: applying`.
   When `status` is already `applying`, the last attempt's process died before recording a result
   (the caller's shell timed out; a full apply takes about an hour), and this call **resumes that
   attempt**: no refusal, no increment. A resumed attempt takes the newest `deploy.yml` run of the
   pushed head even when that run has finished (it finished after the process waiting on it died,
   J-023), and dispatches a run only when there is none.
2. Pushes `factory/<id>` (as `publish` does, without the PR).
3. GitHub, first, so a missing permission fails before anything is created in HCP: ensures the
   environment `preview`, its variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`, the repo variable `TF_CLOUD_ORGANIZATION`, and the secret
   `TF_API_TOKEN` (`gh secret set`, the GitHub PAT in `gh`'s environment only, the value on stdin).
   Azure: ensures the federated credential `preview-<owner>-<name>` on `AZURE_GH_CLIENT_ID`
   (`az ad app federated-credential`, under the human's own `az login`) for the subject GitHub
   presents: `<prefix>:environment:preview`, where `<prefix>` is the repo's `sub_claim_prefix` from
   `GET /repos/<t>/actions/oidc/customization/sub` when it has `use_immutable_subject` (the
   default for new repos: `repo:<owner>@<id>/<name>@<id>`), else `repo:<owner>/<name>`. A
   credential of that name with another subject is updated in place.
4. HCP: ensures the project (`<repo name>`), attaches `HCP_AZURE_VARSET` to it, ensures the
   workspaces `<repo>-dev` and `<repo>-staging` (idle - "ready to deploy once Done"), and ensures
   `<repo>-j<NNN>` with `auto-destroy-at` 24 hours ahead.
5. Dispatches `.github/workflows/deploy.yml` on `factory/<id>` with `environment = j<NNN>`, finds
   that run, and polls it until it completes or the timeout passes. A run on that branch for the
   same head sha that has not completed is polled instead, and nothing is dispatched.
   When the preview was `up` before this attempt and the branch's `deploy.yml` declares a `since`
   input, the dispatch also passes `since = <deploy.sha>`, the head that preview was applied at.
   The workflow uses it to deploy only the front end when nothing outside `src/ui/` changed since
   then (Phase 6, J-022 lessons plan). Otherwise `since` is not sent: GitHub refuses a dispatch
   with an input the workflow does not declare.
6. Applied: reads the workspace's outputs - `react_url` or `blazor_web_url` as `urls.frontend`,
   `api_url` as `urls.api`, and as `urls.health` the address a browser reaches `/health` by:
   `<react_url>/api/health` when there is a React front end, else `<api_url>/health`. React's Static
   Web App links the API as its backend, which puts the API behind the Static Web App's own auth, so
   `<api_url>` itself answers 401 to anything else.
7. Smoke: `GET <urls.health>` and, when there is one, `GET <urls.frontend>` must both answer
   200. A freshly started container can take a while, so each is retried every 10 seconds for up to
   `$env:FACTORY_SMOKE_SECONDS` (default 300). A probe that never answers 200 is a failed attempt,
   and `deploy-<a>.log` ends with a `## Smoke` section: each url, its last status, how long it took,
   its `Server` header and the start of its body. A pass leaves the same lines in
   `jobs/<id>/smoke-<a>.log`. (The templates map `/health` in every environment, not only in
   Development, and serve every route with and without the `/api` prefix.) An idle preview scales to
   zero, and the first request through its Static Web App then answers 500 `Backend call failure`
   after about 45 s, so an agent checking a preview retries too.
7a. Sign-in smoke, when the workspace outputs `qa_user` and `qa_password` (the auth add-on's, the
   ones `qa-login` reads) and there is a front end. `factory/scripts/signin-smoke.mjs` (Playwright,
   headless Chromium) opens `<urls.frontend>`, clicks **Sign in**, completes the identity
   provider's form as the QA user (Entra External ID: email, then password; Keycloak: username and
   password), and passes when the browser is back on the front end's origin with the header showing
   **Sign out** and the app's own `GET /api/me` (which it sends with the access token; `/api/me` is
   `[SignInRequired]`, so a request without one answers 401) answering 200. The password is passed to the
   child on stdin, never as an argument or in the log. `AADSTS9002326` is Entra still replicating a
   new app's SPA client type, which took about 90 s on J-025, so the whole smoke is retried, up to five
   tries, `$env:FACTORY_SIGNIN_RETRY_SECONDS` apart (default 30). Any other error page on the way (an `AADSTS` code,
   an `error=` on the redirect, a timeout of `$env:FACTORY_SIGNIN_SECONDS`, default 120) is a failed
   attempt, as are five of those. `deploy-<a>.log` then ends with a `## Sign-in smoke` section: each step reached, the
   final url without its query, the page's error text, and a screenshot path under `jobs/<id>/`.
   A pass leaves evidence too: `jobs/<id>/signin/smoke-<a>.log` holds the steps (never the
   password), and the smoke saves a screenshot of the signed-in page under `jobs/<id>/signin/`.
   The smoke runs on every apply, not only when auth changed: J-022's sign-in worked on the round-8
   preview and broke on a later apply, which cleared the app's identifier URI. `$env:FACTORY_SIGNIN_CMD`
   replaces the script in tests.
8. Success: records `status: up`, the head `sha`, `appliedAt`, `destroyAt`, appends a `deploy`
   event, **moves the job to `approve`**, and prints the urls.
9. Failure: writes `deploy-<a>.log` (the failed GitHub jobs' logs, then the workspace's latest HCP
   run's plan and apply logs, then the smoke results if it got that far), records `status: failed`,
   appends a `deploy` event, and exits 1 - or, when that was attempt 5, moves the job to `blocked`
   with flag `deploy-failed`. The preview is left up either way, for diagnosis.

**The shell limit.** A deploy can run for up to an hour, and Claude Code cuts a shell call off at 10
minutes by default (J-023). The runner's headless settings raise that with `BASH_MAX_TIMEOUT_MS` (90
minutes), and the devops agent asks for it. A call that is cut off anyway is resumed by running `deploy`
again, as step 1 says.

**The deploy loop.** After every failed attempt the devops agent reads `deploy-log` and diagnoses
the first real error as one of three, and acts:

| Diagnosis | Examples | Action |
| --- | --- | --- |
| infrastructure | Terraform, the workflow, a module input, an image build step | fix `IaC/` or `.github/workflows/`, commit, `deploy` again |
| the change | a migration that fails, an API that crashes on start, a `/health` that is not 200 | write `round-<n>/deploy-fix.md` (the error, the evidence, what the builder should change) and `deploy-fix <id> -File` it. The job goes back to `build` as a new round, and that round is built, fully reviewed and deployed again |
| the environment | a federated credential, a quota, a region outage, a factory bug | `block <id> -Detail "<one line>"`. No agent can fix these; a human fixes the cause and retries |

`deploy-fix` and `block` are refused unless the job is in `deploy` and its last attempt failed, so
an agent cannot skip the deploy. A deploy-fix round is like a rework round: one build and one review
pass, and a `CHANGES` verdict then blocks the job `max-rounds`.

**A missing GitHub PAT permission is not a failed attempt.** When GitHub answers 403 with an
`X-Accepted-GitHub-Permissions` header (or `gh` reports HTTP 403 setting the secret, which needs
`secrets=write`), the error reads `the GitHub PAT needs <permission>`. `deploy` then gives the
attempt back, writes `deploy-<a>.log` as usual, and moves the job to `blocked` with flag
`github-permission` and that sentence as the detail. No agent can widen the PAT: a human adds the
permission (GitHub requires their 2FA for it) and clicks **Retry deploy** (`redeploy <id>`), which
queues the job in Approved to resume at `deploy`. The factory never grants itself permissions.

Every new round starts with `attempts` back at 0. A rework or a deploy-fix round leaves the old
preview up, and the next `deploy` applies over it.

**`factory.ps1 destroy <id>`** queues a destroy run on the workspace (auto-applied), waits for it,
safe-deletes the workspace, and records `status: down` and a `destroy` event. Without a `deploy`, or
when it is already `down`, it does nothing. It never moves the job.
- HCP answers a safe-delete `409` while it processes the state a destroy just wrote (J-025); that is
  retried for up to 5 minutes.
- A workspace no apply ever reached answers the destroy run `422 Configuration version is missing`
  (J-018). It has nothing to destroy, so the workspace is simply deleted.
- A failure puts `status` back as it was (or `failed`, when it was `destroying`), records
  `destroyError`, adds one to `destroyFailures`, and appends a `destroy` event.

**`factory.ps1 sweep`** reconciles the previews that outlived their work. It starts a detached
`destroy` for each job in `done` or `discarded` whose preview is not `down`, and a detached
`canary-destroy` for a canary whose teardown failed. It skips anything on its fifth failure, which
waits for the human, and a destroy that started less than 90 minutes ago, which may still be running.
It prints what it started and does nothing with deploys off. `serve.ps1` runs it at startup and the
runner when it drains. Non-terminal jobs are left alone: their next round applies over the preview,
and a destroy there is the J-022 race.

### Canary

The deploy path is tested against stubs, and stubs encode our assumptions about GitHub, Azure and
HCP. The **canary** tests the real thing: it deploys a fresh scaffold of the current templates
end to end, records the result, then tears it down. Deployable jobs wait until a canary has passed for the current
inputs. (J-019 used up all three of its attempts on three bugs - an OIDC subject format, a
credential that already existed, provider auth in the template - and a canary would have found each
one before any real job paid for it.)

**Settings** (`.env`): `FACTORY_CANARY_REPO` (`owner/name`; default `<GITHUB_OWNER>/factory-canary`)
and `FACTORY_CANARY_SCAFFOLD` (scaffold options as in [Targets](#targets); default
`{"name":"canary","resource":"Item","db":"postgres","ui":"react","events":false,"auth":true}`, so
every canary runs the sign-in smoke, deploy step 7a, against a real Entra External ID sign-in).
A scaffold with auth also needs `FACTORY_ENTRA_TENANT_ID`, `FACTORY_ENTRA_SUBDOMAIN` and
`FACTORY_ENTRA_USER_FLOW_ID` in `.env` (as `setup-azure.ps1` prints them): the canary appends them to
its scaffold's `IaC/envs/ephemeral.tfvars` as the `entra_external_*` variables, which a target
commits itself, and refuses before anything deploys when one is missing (canary 15 failed its
apply, and then its destroy, without them).

**Fingerprint.** A SHA-256 over what the deploy path depends on: `deploy.ps1`, `hcp.ps1` and
`github.ps1`; the template workspace's `IaC/` and `base-project/.github/workflows/deploy.yml`; the
template pack as built (`artifacts/nupkg/*.nupkg`, which is what gets installed); the canary's own
settings; and
the HCP variable set's variables (key, category, and the value unless it is sensitive). A changed
sensitive value does not change the fingerprint; rotate one and run the canary by hand.

A canary runs on the [shared platform](DEPLOY.md#the-shared-platform), as a real job's preview does.
**`-Full`** adds `use_platform = false` to the scaffold's `ephemeral.tfvars`, so it builds the whole
stack. That is the only thing that still proves a from-scratch create, which dev and staging rely on.
Run it by hand after changing template IaC. It takes about 61 minutes (31 before its result), runs
even when up to date, and records `full: true`. If it fails, deployable jobs wait as for any canary.

**`factory.ps1 canary [-Force] [-Full]`** - one run at a time (`canary.lock`, as `runner.lock`):

1. Computes the fingerprint. Without `-Force`, when `canary.json` already records a pass for it,
   prints `canary: up to date` and exits 0. Otherwise it waits for the last canary's teardown, which
   still holds `j000`; a teardown that started over 90 minutes ago is taken to have died.
2. Scaffolds into `<work>\canary` (emptied first): `dotnet new aspire-base -n <name> -o .
   --dbProvider <db> --resource <resource>`, then `aspire-addon add ui-<ui>` (and `add auth` when
   the options say so) - the commands a bootstrap job runs, with no agent.
3. Creates the repo if it is missing (private, as `checkout` does) and force-pushes the scaffold to
   its `main`, where GitHub looks for `deploy.yml`.
4. Deploys it as environment `j000` (workspace `<repo name>-j000`) through the same steps as
   `deploy` 3-7 - GitHub and Azure preparation, HCP, dispatch, outputs, smoke check - with the
   dispatch on `main`.
5. Writes `canary.json` and `canary-<n>.log` (the same sections as `deploy-<a>.log`), appends a
   `canary` event (`pass` or `failed: <first error line>`), and deletes the work directory. A passing
   canary has proved the deploy, and a destroy takes as long again, so nothing waits for it.
6. Releases `canary.lock`, starts `factory.ps1 canary-destroy` detached, and exits 0 on a pass, 1
   otherwise.

**`factory.ps1 canary-destroy`** takes `canary.lock`, destroys `j000` as `destroy` does, pass or fail,
and confirms the workspace is gone. It appends `## Destroy` to the log, records the outcome under
`teardown`, and appends a `destroy` event with `job: "canary"`. A teardown that fails **does not** fail
the canary: the deploy path it tested is proven. It still matters, since discarding a job relies on
the same teardown and `j000` is left standing, so the dashboard shows it in the banner and `sweep`
retries it.

```json
{ "fingerprint": "sha256:...", "status": "running" | "pass" | "failed", "error": null | "<first error line>",
  "repo": "kamabery/factory-canary", "sha": "<scaffold commit>", "urls": { "frontend": "...", "api": "...", "health": "..." },
  "log": "canary-3.log", "startedAt": "...", "finishedAt": "...", "n": 3, "full": false,
  "teardown": { "status": "destroying" | "down" | "failed", "error": null | "<first error line>", "startedAt": "...", "failures": 0 } }
```

**The gate.** `next` does not return a deployable job unless `canary.json` records a pass for the
current fingerprint. When it does not, the runner runs `canary` itself (logging `canary pass` or
`canary failed: <error>`) before taking that job. On a failure, deployable jobs stay in Approved and
the runner takes only jobs that are not deployable (bootstraps); the dashboard shows a banner with
the error and **Run canary**. A failed canary is not retried automatically, because each run costs
real Azure time; the human fixes the cause and clicks **Run canary** (`canary -Force`), and the
watchdog starts the runner once it passes.

**`factory.ps1 qa-login <id>`** prints `qa_user` and `qa_password` from the workspace's outputs (the
auth add-on's), or refuses when the preview has none. Only the dashboard calls it, for the human.

## Dashboard API

`serve.ps1` listens on localhost only. GETs serve `factory/dashboard/index.html`, the files under
`factory/dashboard/vendor/` (at `/vendor/...`) and read-only files under the state dir: it rejects
`..`, and serves `.md` as text/plain. Every write is a thin
wrapper over one `factory.ps1` call. The response is `{ "ok": bool, "message": "<stderr or stdout>" }`,
and a refused move snaps the card back and shows the message.

| Request | Calls | Then |
| --- | --- | --- |
| `GET /api/jobs/<id>/files` | (read-only) | the job's artifact paths, relative to `jobs/<id>/` |
| `GET /api/agents/<agentId>/transcript` | (read-only) | the `transcript` file named by that agent's `agent-start` event; no other path is served |
| `PUT /api/jobs/<id>/spec` (text/markdown) | `edit-spec` | |
| `POST /api/jobs/<id>/move` `{ "to": "approved" }` from backlog | `approve` | starts the runner |
| `POST /api/jobs/<id>/move` `{ "to": "approved", "note" }` from ready or blocked | `rework` | starts the runner. The rework dialog has the factory-fault hint (see [Blocked](#blocked)) |
| `POST /api/jobs/<id>/move` `{ "to": "done" }` | `merge` | |
| `POST /api/jobs/<id>/move` `{ "to": "discarded" }` | `discard` | |
| `POST /api/jobs/<id>/redeploy` | `redeploy` | **Retry deploy** on a blocked card; starts the runner |
| `POST /api/jobs/<id>/resume` | `resume` | **Resume** on a card flagged `usage-limit`; starts the runner |
| `POST /api/canary` | `canary -Force`, started detached | **Run canary** on the canary banner; 409 while `canary.lock` is held. The banner reads `/state/canary.json` and shows the error and the log link |
| `POST /api/purge` | `purge` | |
| `POST /api/jobs/<id>/destroy` | `destroy`, started detached | **Tear down** on a card with a live preview; answers at once, the card shows `destroying` until the board says `down` |
| `GET /api/jobs/<id>/qa-login` | `qa-login` | `{ ok, message }` with the user and password; the card's **QA login** shows it, never stores it |
| `GET /api/lessons` | (read-only) | every `lessons/<id>.json`, newest job first; the markdown is at `/state/lessons/<id>.md` |
| `POST /api/lessons/<id>/mark` `{ status, session? }` | `lesson-mark` | |
| `GET /api/evals` | (read-only) | every `evals/<id>.json`, newest job first; the markdown is at `/state/evals/<id>.md` |
| `PUT /api/evals/<id>/feedback` (text/markdown) | `feedback` | the feedback is at `/state/evals/<id>.feedback.md` |
| `GET /api/ideate/config` | (read-only) | `{ enabled, model, owner, templatesRepo, deploys, mode, provider }`; `enabled` means the mode's idea-turn credential is configured (see [Idea sessions](#idea-sessions-dashboard)); `mode` and `provider` are the [run mode](#run-mode-and-providers) and its provider, shown in the dashboard header; `owner` is `GITHUB_OWNER`; `templatesRepo` is `FACTORY_TEMPLATES_REPO`; `deploys` means `.env` has `HCP_ORG` (Approved cards' wait reasons); no key is ever returned |
| `GET /api/sessions` | (read-only) | every session that is not `closed`: `{ id, target, text, status, idea, bootstrap, costUsd, newTarget }` |
| `GET /api/repos` | `list-repos` | the JSON array, for the New idea picker |
| `POST /api/sessions` `{ target, text, base? }` or `{ new: true, text }` | `check-target` (not for `new`, which needs `GITHUB_OWNER`), then `clone-target [-Base]` | allocates `S-NNN`, writes `session.json` (with `base`) and the first message, which tells the model the clone is the base's unmerged branch, and starts a turn. The panel's **Base** list is `main` plus every job on the target that is not `done` or `discarded` and has a PR |
| `POST /api/sessions/<S>/reply` `{ text, scaffold? }` | (session write); `check-target` when the session has no target | answers `pending` (or adds user text), stores `scaffold`, starts a turn; with no target, `scaffold.repo` sets it and must name a new repo; empty text with no pending retries a failed turn |
| `POST /api/sessions/<S>/brief` | `new-idea`, then writes `brief.md` | existing target: answers `submit_brief` and starts the spec turn; new target: records the bootstrap job |
| `POST /api/sessions/<S>/write-specs` | (session write) | after the bootstrap merge (the idea is `drafting`): starts the spec turn |
| `POST /api/sessions/<S>/specs/<k>` `{ decision }` | `add-spec` (+ `approve`) | `approved` starts the runner; `rejected` calls nothing |
| `PUT /api/sessions/<S>/specs/<k>` (text/markdown) | (session write) | the human's direct edit of `spec-<k>.md` |
| `POST /api/sessions/<S>/close` `{ failed, reason? }` or `{ abandon: true }` | `idea-done [-Failed -Reason]`, or `idea-abandon` (only once there is an idea) | the session becomes `closed`; the default reason is "every spec rejected in session". `abandon` is the session card's **Abandon**, refused while a turn is running |
| `POST /api/ideas/<I>/abandon` | `idea-abandon` | **Abandon** on an idea card |

Session reads use `/state/sessions/<S>/...`. An idea card opens its brief. A card for an open
session, or for an idea that a session created, reopens the session panel. An idea card from the
CLI session shows the command that resumes it.

"Starts the runner" means: spawn `runner.ps1` detached, unless `runner.lock` is held. The
[watchdog](#runner) also starts it when Approved is not empty and no live runner holds the lock. The claude
command is taken from `$env:FACTORY_CLAUDE_CMD` when it is set, so tests spend no tokens.

The header's **Board** and **Evaluations** tabs switch the page. Evaluations lists every
evaluation with its rating, the human's rating if given, and snapshot facts, and opens one to its
markdown. It also lists every `done` or `discarded` job on the board that has no evaluation yet,
rated `not evaluated`, newest job first with the rest; opening one shows the `factory.ps1 eval`
command that evaluates it. Below the markdown, **Human feedback** is a rating and a note that **Save feedback** sends
as `Rating: <r>`, a blank line, then the note.

The job modal's **Output** tab tails `run-<n>.jsonl` (the orchestrator) and each agent's
`transcript`. It shows narration, tool calls and results. Claude Code writes thinking blocks with
empty text, so a thinking block shows only as a marker.

## Runner

`runner.ps1` drains the Approved queue one job at a time. It exits at once if it cannot create
`runner.lock` (an exclusive file create holding its PID). It takes over a lock whose PID is no
longer running. Otherwise it loops:

1. `factory.ps1 next -NeedsCanary`. If it prints `canary`, the runner runs `factory.ps1 canary` and
   logs the outcome; after a pass it goes back to step 1, after a failure it takes only what plain
   `next` returns. If nothing comes back, it deletes the lock and exits.
2. It calls `factory.ps1 set-stage <id> build -By runner` (or the stage named by the job's `resume`), then `factory.ps1 checkout <id>`, which
   creates the GitHub repo if needed and clones or reuses `<work>\jobs\<id>` on `factory/<id>` (the
   pushed branch when the job is reworked, otherwise a new one from `origin/main`). It then fetches
   `origin` and points `refs/remotes/factory/base` at the job's base branch as it is now (`main`, or
   an unmerged base job's branch), and records the branch head as `roundStart`. The builder's first step is `git merge --no-edit factory/base`,
   resolving any conflicts and noting them in build.md, so a branch cut before other work merged
   is reviewed, previewed and gated with that work in it (J-022).
3. It sets `FACTORY_JOB=<id>`, `FACTORY_HOME=<this repo>\.factory-runs` and `FACTORY_REPO=<this repo>`,
   writes `<stateDir>/headless-settings.json`, and runs this in the job's work directory:
   `claude -p "/factory:factory run <id> <stateDir> <factory.ps1>" --plugin-dir <this repo>/factory/plugin --plugin-dir <template workspace> --settings <stateDir>/headless-settings.json --add-dir <stateDir> --output-format stream-json --verbose --permission-mode acceptEdits`
   (`claude-run.ps1` builds this command for both scripts).
   - `headless-settings.json` `permissions.allow` lets agents run the toolchain (`dotnet`, `npm`,
     `npx`, `node`, `aspire`, `aspire-addon`, `terraform init -backend=false`, `terraform validate`,
     `terraform test`, `terraform fmt`, each also as `terraform -chdir=<dir> …`, and `curl` for the approver's smoke test), git reads and commits (`add`, `commit`, `rm`, `revert`), one merge (`git merge --no-edit factory/base`,
     and `git merge --abort`), and read-only shell commands, in both the Bash and the
     PowerShell tool; any other merge is unmatched and so refused, and `deny` blocks `git push`, `git merge main`/`origin…`, `rebase`, `reset --hard`, switching to
     `main` and `git worktree`, `terraform … apply`/`plan`/`destroy` (a `-chdir=*` rule's wildcard would
     otherwise match `terraform -chdir=x apply … validate`), and reading the environment (`printenv`, `env`, and
     `Get-ChildItem`/`Get-Item`/`Get-Content` on `env:`), which may hold credentials the human never meant an agent to read. `additionalDirectories` makes `factory/templates` and the template
     workspace readable. The allowlist also holds the absolute
     `factory.ps1` path. The skill calls `factory.ps1` by that absolute path with no
     `$VARIABLES`, because a headless permission check can't approve a variable it can't see.
   - `permissions.deny` is the fence: `Edit` on `/factory/**`, `/.claude/**` and
     `/docs/FACTORY-CONTRACT.md` in the work directory, the same three by absolute path in this repo,
     `<stateDir>/jobs/*/spec.md`, and the template workspace; and `Read` of this repo's `.env`.
     Agents never push: only `factory.ps1` holds the token.
   - `claude` runs with its mode's child environment (see [Run mode and providers](#run-mode-and-providers)).
   - claude's output is read as UTF-8 whatever the console's codepage.
   - stderr goes to the same log, so a reader skips lines that are not JSON.
4. It appends each stdout line to `jobs/<id>/run-<n>.jsonl` as it arrives, through one stream opened for the whole pass and shared for reading and writing (and `serve.ps1` reads state files shared the same way), so a reader can never fail the job. It calls `set-cost`
   with `total_cost_usd` and `duration_ms` from the final `"type":"result"` line. If the job is
   in `gate` afterwards, the approver has approved and only the gate is left, so the runner runs
   `factory.ps1 gate <id>` itself. A claude that dies at that point (out of credit, say) costs
   nothing. If the job is still in Building (and not `blocked`) after that, whether claude exited
   non-zero or not, it escalates the job (`set-stage <id> ready -Reason escalated`, which lands a
   deployable job outside `approve` and `gate` in `blocked`). That way the queue never stalls. When claude's
   result line is an error (`is_error`), its text ends the flag detail (`claude exited with exit 1:
   Credit balance is too low; see run-<n>.jsonl`), so the card says why. A **usage limit** is not an
   escalation: when the result line has `api_error` `usage_limit_reached` (the subscription's limit)
   or `api_error_status` 429 (a Foundry quota), the runner flags the job `usage-limit` with the
   result text and its `session_id`, skips `publish` and `lesson` for it (the job is unfinished), and
   ends the drain (`exit usage-limit`); `next` then holds the queue until the human resumes. A job
   queued by `resume` runs as `claude -p --resume <resumeSession> "<continue prompt>"` with the same
   plugins and settings, in the same work directory, so the orchestrator picks up where it stopped.
   Once the job is
   in `ready` or `blocked`, it calls `factory.ps1 publish <id>`; a failed publish is logged (`publish <id>:
   <message>`) and leaves the job as it is, since `merge` publishes again. Then it calls
   `factory.ps1 lesson <id>` ([Lessons](#lessons)); a failure there is logged the same way. The runner runs
   `checkout` and `publish` in a child `pwsh`, so it can read the refusal `factory.ps1` writes to stderr.
5. An error while handling one job does not end the runner. It logs the error, moves the job to
   `ready` with flag `escalated` if it is still in Building (detail `runner error: <message>`), and
   goes back to step 1.
6. It appends to `<stateDir>/runner.log`, one timestamped line each: `start pid=<pid>`,
   `take <id>`, `checkout <id> failed: <message>` (the job is then escalated with detail
   `work dir: <message>`), `claude <id> exit=<code> result=<yes|no>`, `stage <id> <stage>`,
   `error <id>: <message>` followed by the stack, and how it ended: `exit drained`, `exit usage-limit`, `exit fatal: <message>`,
   or `exit stopped` (interrupted, e.g. its console closed, before the loop finished).

**Watchdog.** `serve.ps1` checks the board every 10 seconds (`-WatchdogSeconds`). If a job is in
`approved` and `runner.lock` is missing or names a PID that is no longer running, it starts
`runner.ps1` detached and logs `watchdog: started runner`. A runner that died, or one that was
never started (an approval made outside the dashboard), costs one interval, not a stalled queue.

**PR sync.** Every 30 seconds (`-PrSyncSeconds`), if a `ready` job has a PR, `serve.ps1` starts
`factory.ps1 sync-prs` detached. A PR the human merged on GitHub then lands its job in Done within
one interval, with no drag.

The `/factory:idea` session doesn't use the runner or the lock. It writes only under `ideas/` and
through `factory.ps1`, so it can run while a job builds. After it approves a spec it starts
`runner.ps1` detached, as serve.ps1 does.

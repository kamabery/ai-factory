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
               (edit spec)             (FIFO queue)        build         (flag if the       (merge)
                  │                         ↑              review ×4      factory gave up)
                  └──discard                │              approve            │
                                            │              gate               ├──discard
                                            └────────── rework <note> ────────┘
```

Done and discarded cards stay until a human clears them ([Purge](#purge), the Done column's **Clear**).

## Board columns and stages

| Column | Stages | Who moves cards out |
| --- | --- | --- |
| Ideas | `idea` records with status `waiting`, `drafting` or `failed`, and open dashboard [idea sessions](#idea-sessions-dashboard) (not jobs) | the idea session |
| Backlog | `backlog` | human (drag to Approved, or discard) |
| Approved | `approved` | runner |
| Building | `build`, `review`, `approve`, `gate` | orchestrator / script |
| Ready for review | `ready` | human (drag to Done, rework, or discard) |
| Done | `done` | nobody (terminal) |
| (hidden) | `discarded` | nobody (terminal) |

In Building, the card shows its sub-stage, its round and the reviewer chips. Only Backlog,
Approved, Ready for review and Done accept drops.

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
  through the `aspire-addon` CLI and the gate adds `dotnet build .aspire/apphost.cs`. The idea waits (`waiting`) until that job is
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

The template workspace is `$env:FACTORY_TEMPLATES`, else `<parent of this repo>\template-projects`.
Agents may read it and never edit it. It is also a plugin (`aspire-starter`, its own
`.claude-plugin/plugin.json`), loaded into every headless pass; see [Runner](#runner).

**Work directories.** Code changes and testing happen in a local temp clone per job,
`<work>\jobs\<id>`, where `<work>` is `$env:FACTORY_WORK`, else `<system temp>\claude-factory`.
`checkout` clones the target there and checks out `factory/<id>`. The clone is deleted when the job
moves to `done` or `discarded`. It survives a rework, and if it is gone `checkout` clones again from
the pushed branch. One job builds at a time across all targets. Idea sessions read a shallow clone of
an existing target (`clone-target`) in `<work>\sessions\<S>` (dashboard) or
`<work>\cli\<owner>-<name>` (`idea.ps1`), deleted when the session closes.

The spec carries the target and the gate, and the human can edit both in Backlog:

```
## Target
- Repo: kamabery/todo-app

## Gate
- build: `dotnet build`
- test: `dotnet test`
```

Gate lines are ``- <name>: `<command>` ``, where `<name>` is lower-case letters, digits or `-`. The
commands run with `pwsh -Command` in the job's work directory. The human approves them along with the spec, and
no agent can edit `jobs/<id>/spec.md`.

A dependency install in a gate is guarded, so the gate does not reinstall on every run. For npm,
that means `npm ci` runs only when `node_modules/.package-lock.json` (which npm writes at the end
of an install) is missing or older than `package-lock.json`:

```
- ui: `Set-Location src/ui/react; if (-not (Test-Path node_modules/.package-lock.json) -or (Get-Item package-lock.json).LastWriteTime -gt (Get-Item node_modules/.package-lock.json).LastWriteTime) { npm ci }; npm run build`
```

## Providers

Every job has a **provider**, the endpoint its `claude` pass talks to: `subscription` (the CLI's
own login, the default) or `openrouter` (Claude models through OpenRouter's Anthropic-compatible
API). A job's builder, reviewers and approver are subagents of that one pass, so they share it.
Idea sessions are not affected.

- `add-spec` stamps `provider` from `FACTORY_PROVIDER` in `.env` (`subscription` when unset). Any
  other value is refused. `set-provider <id> <provider>` changes it while the job is in `backlog` or
  `approved`.
- For an `openrouter` job the runner reads `OPENROUTER_API_KEY` from `.env` into a local variable and
  sets, for the `claude` child only, `ANTHROPIC_BASE_URL=https://openrouter.ai/api`,
  `ANTHROPIC_AUTH_TOKEN=<key>` and an empty `ANTHROPIC_API_KEY`, plus any of
  `ANTHROPIC_DEFAULT_FABLE_MODEL`, `ANTHROPIC_DEFAULT_OPUS_MODEL`, `ANTHROPIC_DEFAULT_SONNET_MODEL`,
  `ANTHROPIC_DEFAULT_HAIKU_MODEL` and `CLAUDE_CODE_SUBAGENT_MODEL` that `.env` sets. It restores its
  own environment afterwards. The token takes precedence over the CLI's claude.ai login, which also
  turns off claude.ai connectors for that pass. A missing key escalates the job (`provider: no
  OPENROUTER_API_KEY in .env`) without running `claude`.
- The key is in the agents' environment, so the headless settings deny reading the environment
  (see [Runner](#runner)), and the key should be a dedicated one with a credit limit
  ([OPENROUTER.md](OPENROUTER.md)).
- `costUsd` is `claude`'s own estimate from list prices whatever the provider; the `cost` event
  carries `provider`, so OpenRouter's bill can be reconciled against it later.

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
                  "scaffold": null, "status": "waiting|drafting|done|failed", "jobs": ["J-004", "J-005"],
                  "createdAt": "..." }
```

- `factory.ps1 new-idea <text> -Target <path> [-Scaffold <json>]` validates the target and options
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
- `factory.ps1 idea-done <ideaId> [-Failed -Reason <text>]` closes the idea. Ideas in `done`
  are no longer shown; ideas in `failed` stay in Ideas with the reason until a `purge`.

## Idea sessions (dashboard)

The dashboard's idea session runs the steps of [Ideas](#ideas) against the Claude API, not the
`claude` CLI. Build, review, approve and gate still run on the CLI through the runner. A session is
a conversation stored under `sessions/<S-NNN>/`. It is not on the board, and **the model never
changes board state**: every board write in a session is a human click that `serve.ps1` turns into
one `factory.ps1` call.

**The key.** The API key is `ANTHROPIC_API_KEY` in `.env` at the repo root (gitignored;
`.env.example` names the keys). `FACTORY_IDEA_MODEL` there overrides the model (default
`claude-opus-5-5`), and `ANTHROPIC_BASE_URL` the endpoint (tests point it at a stub). The key is
read from the file into a local variable by `idea-turn.ps1` and never set in the process
environment, so a `claude` started by the runner keeps using the subscription. `serve.ps1` only
checks that the key is present. With no key, the dashboard shows the `idea.ps1` hint instead of
**New idea**.

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

**Session flow.** The human picks a target from their GitHub repos (`list-repos`) and describes the
idea. `check-target` classifies the target, and the panel creates the session. Or the human picks
**Add new**: the session starts with no target (`target: null`, `newTarget: true`), and the model
grills first, then names the repo in `propose_scaffold`. Confirming that form runs `check-target`
on `<GITHUB_OWNER>/<repo>`, which must be `new`, and sets `target`; `scaffold` is stored without
`repo`. For an existing target, `serve.ps1` runs `clone-target`
into `<work>\sessions\<S>` and records it as `checkout`; **Write specs** clones again (the bootstrap
has merged), and closing the session deletes the clone.

1. The model grills (`ask_questions`) and, for a new target, calls `propose_scaffold`. The human
   confirms or edits the form.
2. The model calls `submit_brief`. The panel shows it with **Confirm** and **Correct**. Correct
   sends the human's words back as the tool result.
3. Confirm runs `new-idea` (with `-Scaffold` for a new target) and writes `ideas/<I>/brief.md`.
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
| build | review | `round-<n>/build.md` exists | |
| review | build | all 4 verdicts in, at least one `CHANGES`, round < 2; round += 1, reviews reset to `pending` | |
| review | ready | at least one `CHANGES` at round ≥ 2 | `max-rounds` |
| review | approve | all 4 verdicts are `PASS` | |
| approve | gate | `decision.md` first line is `Decision: APPROVE` | |
| approve | ready | `Decision: ESCALATE` | `escalated` |
| gate | ready | every gate command exited 0 (no flag), or any one did not. Run by the orchestrator, or by the runner when claude ended with the job in `gate` | `gate-failed` |
| build / review / approve | ready | the orchestrator gives up, or the runner's `claude` process ends with the job still here | `escalated` |
| ready | done | human `merge`, or `sync-prs` after the human merged the PR on GitHub; see [Gate and merge](#gate-and-merge). Merges the PR (or records the GitHub merge) and deletes the work directory. A bootstrap job's merge moves its idea `waiting` → `drafting` and prints `released <ideaId>` | |
| ready | ready | human `merge` refused: nothing to merge, uncommitted changes in the work directory, PR not mergeable / red gate | `merge-blocked` / `gate-failed` |
| ready | approved | human `rework <id> <note>`; writes `jobs/<id>/rework-<n>.md`, round += 1, sets `approvedAt` | |
| ready | discarded | human; closes the PR, deletes branch `factory/<id>` on GitHub and the work directory | |

`done` and `discarded` are terminal. `flag` is cleared on every move out of `ready`.

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
`discarded` that has an evaluation (`evals/<id>.json`), and every idea in `done` or `failed` none of whose jobs is left. For each it deletes
`jobs/<id>/` or `ideas/<id>/`, and for a job also a work directory still left at `<work>\jobs\<id>`
(one that a locked file kept `merge` or `discard` from deleting). It appends one `note` event (`purged J-001, ...`) and
prints the removed ids, one per line. Ideas in `waiting` or `drafting` and jobs in any other stage
are untouched. A finished job with no evaluation stays on the board, with its `jobs/<id>/`, until it
is evaluated: purging it would delete the run logs an evaluation is written from. `events.jsonl`
keeps its history, and `board.seq` stops a purged id being reused.

## Files

```
.factory-runs/                 gitignored; FACTORY_HOME overrides its location
  board.json
  events.jsonl
  runner.lock                  held while runner.ps1 is draining the queue
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
    round-<n>/
      build.md
      review-security.md  review-code.md  review-ux.md  review-ui.md
    decision.md
    gate.log
    run-<n>.jsonl              the claude -p stream-json output of each runner pass, written live
  evals/                       kept by purge (see Evaluations)
    <id>.md  <id>.json         the evaluation, and the board facts snapshotted when it was written
    <id>.feedback.md           the human's feedback on it, if any

<work>\                       $env:FACTORY_WORK, else <system temp>\claude-factory
  jobs\<id>\                   the job's clone of its target, on factory/<id>; deleted at done / discarded
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
      "kind": "feature",
      "target": "kamabery/todo-app",
      "provider": "subscription",
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

- `reviews.*` is one of `pending | running | PASS | CHANGES`, and it resets to `pending` whenever
  a new round starts.
- `gate` is `null` until the gate runs, then `{ "build": 0, "test": 0, "ui": 0, "sha": "<branch head>", "at": "..." }`
  (process exit codes).
- `flag` is `null` or `{ "reason": "<code from the table>", "detail": "<one line>" }`. Only a
  `ready` job carries one. When the approver escalates, `read-decision` copies the decision's
  `## Escalation reason` section into the flag: its first line is `detail`, and the whole section
  is `why` (markdown). An empty or `N/A` section leaves `detail` as `approver escalated; see
  decision.md` and no `why`. The job modal shows what the reason code means, then `detail`, then `why`.
- `merged` is `null` until a merge, then `{ "sha": "<merge commit>", "repo": "<owner/name>", "pr": "<PR url>", "added": ["<repo-relative path>", ...], "modified": [...] }`,
  from `git diff --name-status --no-renames <sha>^1 <sha>` in the work directory after fetching the merge.
- `pr` is `null` (or absent) until `publish` opens the job's pull request.
- `kind` is `feature` (from a spec-writer spec) or `bootstrap` (generated by `new-idea`); absent means `feature`.
- `provider` is `subscription` or `openrouter` (see [Providers](#providers)); absent means `subscription`.
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

`kind` is one of `idea | stage | agent-start | agent-stop | gate | merge | cost | note | eval | feedback`. Spec-writer
agent events carry `"idea"` instead of `"job"`. `agent` is the plain agent name (the hook strips the
plugin's `factory:` prefix). `agent-start` carries `agentId` and `transcript`, the subagent's live
Claude Code transcript, which is derived from the hook's `transcript_path` and `agent_id`.

## `factory.ps1` commands

State resolves from `$env:FACTORY_HOME`, else `<main checkout>/.factory-runs` (found through
`git rev-parse --git-common-dir` from this repo; the runner sets both for agents in a work directory). Writes to `board.json` are
atomic (a temp file, then a rename; the rename is retried for up to 2 s while a reader such as
`serve.ps1` holds the file). A refused command exits non-zero with a one-line reason on
stderr and leaves the board unchanged.

| Command | Used by |
| --- | --- |
| `new-idea <text> -Target <path> [-Scaffold <json>]` · `add-spec <ideaId> <file>` · `idea-done <ideaId> [-Failed -Reason]` | `/factory:idea`, serve.ps1 (idea sessions) |
| `check-target <owner/name>` (prints `new` or `existing` from the GitHub API, or refuses as `new-idea` would) · `clone-target <owner/name> <dir>` (replaces `<dir>` with a `--depth 1` clone of `main`) | serve.ps1 (idea sessions), idea.ps1 |
| `list-repos` (prints a JSON array of the token user's own repos, `owner/name`, newest push first, at most 100) | serve.ps1 (New idea) |
| `edit-spec <id> -File <path>` (backlog only) · `set-provider <id> <subscription\|openrouter>` (backlog or approved) | serve.ps1 |
| `approve <id>` | `/factory:idea` (on the human's yes), serve.ps1 (human drag) |
| `rework <id> <note>` · `discard <id>` · `merge <id>` · `purge` | serve.ps1 (human drags and **Clear**) |
| `sync-prs` (moves each `ready` job whose PR was merged on GitHub to `done`; prints their ids) | serve.ps1 (PR sync) |
| `next` (prints the next runnable approved id, or nothing) · `checkout <id>` (creates the GitHub repo if needed, then clones or reuses `<work>\jobs\<id>` on `factory/<id>`; prints the work directory) · `publish <id>` (`ready` only: pushes `factory/<id>` and opens the job's PR if it has none; prints the PR url) · `set-cost <id> <usd> <ms>` | runner.ps1 |
| `set-stage <id> <to> [-By] [-Reason] [-Detail]` · `set-review <id> <role> <state>` · `read-reviews <id>` · `read-decision <id>` · `gate <id>` | `/factory run` |
| `event <json \| -Job -Idea -Kind -Agent>` | hook-event.ps1 |
| `eval <id> -File <path>` | a human session evaluating a finished job (see [Evaluations](#evaluations)) |
| `feedback <id> -File <path>` | serve.ps1 (the human's feedback on an evaluation) |
| `status [-Json]` | anyone |

`read-reviews` and `read-decision` exit 2 when a verdict line is missing or malformed; the job
does not move. `set-stage` only performs `approved → build`, `build → review` and
`Building → ready -Reason escalated`; every other transition has its own command.

Overrides, for tests: `$env:FACTORY_GATE_CMDS` (JSON `[{ "name", "cmd" }]`; wins over the spec's
`## Gate`, so tests can use `exit 0` / `exit 1`), `$env:FACTORY_WORK` (the work root),
`$env:FACTORY_ENV_FILE` (the `.env` holding the GitHub settings) and `$env:FACTORY_REPO` (this
repo's main checkout).

## Agents

| Agent | Model | Reads | Writes |
| --- | --- | --- | --- |
| spec-writer | opus | idea.md, brief.md, the target repo | ideas/<id>/spec-1.md .. spec-N.md (N ≤ 5) |
| builder | opus | spec.md, previous round's reviews, rework note | source on the job branch, round-n/build.md |
| security-reviewer | opus | spec.md, the diff, build.md | round-n/review-security.md |
| code-reviewer | sonnet | same | round-n/review-code.md |
| ux-reviewer | sonnet | same | round-n/review-ux.md |
| ui-reviewer | sonnet | same + the target's docs/DESIGN.md, if any | round-n/review-ui.md |
| approver | opus | spec.md, every build.md and review in the job | decision.md |

Every agent works in the job's work directory and follows the target's `CLAUDE.md`, if it has one.
Reviewers inspect the diff (`git diff main...HEAD`) and re-run the spec's Gate commands themselves.
They treat `build.md`'s claims as claims, not as evidence.

## Gate and merge

`factory.ps1 gate <id>` runs the gate commands in the job's work directory on `factory/<id>`, in
order (cloning it again first if it is gone). It writes the output to `gate.log` and records the exit
codes and the branch head `sha`. The commands come from `$env:FACTORY_GATE_CMDS` if set, else from
the spec's `## Gate`.

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
| `POST /api/jobs/<id>/provider` `{ "provider": "openrouter" }` | `set-provider` | the card's provider chip (backlog and approved) toggles it |
| `POST /api/jobs/<id>/move` `{ "to": "approved" }` from backlog | `approve` | starts the runner |
| `POST /api/jobs/<id>/move` `{ "to": "approved", "note" }` from ready | `rework` | starts the runner |
| `POST /api/jobs/<id>/move` `{ "to": "done" }` | `merge` | |
| `POST /api/jobs/<id>/move` `{ "to": "discarded" }` | `discard` | |
| `POST /api/purge` | `purge` | |
| `GET /api/evals` | (read-only) | every `evals/<id>.json`, newest job first; the markdown is at `/state/evals/<id>.md` |
| `PUT /api/evals/<id>/feedback` (text/markdown) | `feedback` | the feedback is at `/state/evals/<id>.feedback.md` |
| `GET /api/ideate/config` | (read-only) | `{ enabled, model, owner }`; `enabled` means `.env` has an Anthropic key; `owner` is `GITHUB_OWNER`; no key is ever returned |
| `GET /api/sessions` | (read-only) | every session that is not `closed`: `{ id, target, text, status, idea, bootstrap, costUsd, newTarget }` |
| `GET /api/repos` | `list-repos` | the JSON array, for the New idea picker |
| `POST /api/sessions` `{ target, text }` or `{ new: true, text }` | `check-target` (not for `new`, which needs `GITHUB_OWNER`) | allocates `S-NNN`, writes `session.json` and the first message, starts a turn |
| `POST /api/sessions/<S>/reply` `{ text, scaffold? }` | (session write); `check-target` when the session has no target | answers `pending` (or adds user text), stores `scaffold`, starts a turn; with no target, `scaffold.repo` sets it and must name a new repo; empty text with no pending retries a failed turn |
| `POST /api/sessions/<S>/brief` | `new-idea`, then writes `brief.md` | existing target: answers `submit_brief` and starts the spec turn; new target: records the bootstrap job |
| `POST /api/sessions/<S>/write-specs` | (session write) | after the bootstrap merge (the idea is `drafting`): starts the spec turn |
| `POST /api/sessions/<S>/specs/<k>` `{ decision }` | `add-spec` (+ `approve`) | `approved` starts the runner; `rejected` calls nothing |
| `PUT /api/sessions/<S>/specs/<k>` (text/markdown) | (session write) | the human's direct edit of `spec-<k>.md` |
| `POST /api/sessions/<S>/close` `{ failed, reason? }` | `idea-done [-Failed -Reason]` (only once there is an idea) | the session becomes `closed`; the default reason is "every spec rejected in session" |

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

1. `factory.ps1 next`. If nothing comes back, it deletes the lock and exits.
2. It calls `factory.ps1 set-stage <id> build -By runner`, then `factory.ps1 checkout <id>`, which
   creates the GitHub repo if needed and clones or reuses `<work>\jobs\<id>` on `factory/<id>` (the
   pushed branch when the job is reworked, otherwise a new one from `origin/main`).
3. It sets `FACTORY_JOB=<id>`, `FACTORY_HOME=<this repo>\.factory-runs` and `FACTORY_REPO=<this repo>`,
   writes `<stateDir>/headless-settings.json`, and runs this in the job's work directory:
   `claude -p "/factory:factory run <id> <stateDir> <factory.ps1>" --plugin-dir <this repo>/factory/plugin --plugin-dir <template workspace> --settings <stateDir>/headless-settings.json --add-dir <stateDir> --output-format stream-json --verbose --permission-mode acceptEdits`
   (`claude-run.ps1` builds this command for both scripts).
   - `headless-settings.json` `permissions.allow` lets agents run the toolchain (`dotnet`, `npm`,
     `npx`, `node`, `aspire`, `aspire-addon`), git reads and commits, and read-only shell commands, in both the Bash and the
     PowerShell tool; `deny` blocks `git push`, `merge`, `rebase`, `reset --hard`, switching to
     `main` and `git worktree`, and reading the environment (`printenv`, `env`, and
     `Get-ChildItem`/`Get-Item`/`Get-Content` on `env:`), which holds an `openrouter` job's key. `additionalDirectories` makes `factory/templates` and the template
     workspace readable. The allowlist also holds the absolute
     `factory.ps1` path. The skill calls `factory.ps1` by that absolute path with no
     `$VARIABLES`, because a headless permission check can't approve a variable it can't see.
   - `permissions.deny` is the fence: `Edit` on `/factory/**`, `/.claude/**` and
     `/docs/FACTORY-CONTRACT.md` in the work directory, the same three by absolute path in this repo,
     `<stateDir>/jobs/*/spec.md`, and the template workspace; and `Read` of this repo's `.env`.
     Agents never push: only `factory.ps1` holds the token.
   - For an `openrouter` job, `claude` runs with the OpenRouter environment of [Providers](#providers).
   - claude's output is read as UTF-8 whatever the console's codepage.
   - stderr goes to the same log, so a reader skips lines that are not JSON.
4. It appends each stdout line to `jobs/<id>/run-<n>.jsonl` as it arrives, through one stream opened for the whole pass and shared for reading and writing (and `serve.ps1` reads state files shared the same way), so a reader can never fail the job. It calls `set-cost`
   with `total_cost_usd` and `duration_ms` from the final `"type":"result"` line. If the job is
   in `gate` afterwards, the approver has approved and only the gate is left, so the runner runs
   `factory.ps1 gate <id>` itself. A claude that dies at that point (out of credit, say) costs
   nothing. If the job is still in Building after that, whether claude exited non-zero or not, it
   moves the job to `ready` with flag `escalated`. That way the queue never stalls. Once the job is
   in `ready`, it calls `factory.ps1 publish <id>`; a failed publish is logged (`publish <id>:
   <message>`) and leaves the job as it is, since `merge` publishes again. The runner runs
   `checkout` and `publish` in a child `pwsh`, so it can read the refusal `factory.ps1` writes to stderr.
5. An error while handling one job does not end the runner. It logs the error, moves the job to
   `ready` with flag `escalated` if it is still in Building (detail `runner error: <message>`), and
   goes back to step 1.
6. It appends to `<stateDir>/runner.log`, one timestamped line each: `start pid=<pid>`,
   `take <id>`, `checkout <id> failed: <message>` (the job is then escalated with detail
   `work dir: <message>`), `claude <id> exit=<code> result=<yes|no>`, `stage <id> <stage>`,
   `error <id>: <message>` followed by the stack, and how it ended: `exit drained`, `exit fatal: <message>`,
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

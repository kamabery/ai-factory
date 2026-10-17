# Factory contract

This file is what the factory's moving parts agree on: the dashboard, `serve.ps1`, `runner.ps1`,
the orchestrator skill, the agents, `factory.ps1` and the hooks. When any of them disagrees with
this file, this file wins, and the disagreement is a bug in the other one. The data formats can
change, but only by editing this file first.

## Principle

Agents propose and execute; `factory.ps1` attests and constrains; a human owns intent and the
release. A human touches the factory at exactly two points: **approving a spec**, in the
`/factory:idea` session (or by dragging a Backlog card to Approved), and **accepting the work**
(Ready for review → Done). Everything between those two is automated.

No agent, skill or endpoint writes to `.factory-runs/board.json`. Every state change is a
`factory.ps1` call, and the script refuses the ones this contract does not allow.

## The flow

```
 /factory:idea session ──── human "yes" per spec ────┐
 grill → brief → spec-writer                         │
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
| Ideas | `idea` records with status `waiting`, `drafting` or `failed` (not jobs) | the `/factory:idea` session |
| Backlog | `backlog` | human (drag to Approved, or discard) |
| Approved | `approved` | runner |
| Building | `build`, `review`, `approve`, `gate` | orchestrator / script |
| Ready for review | `ready` | human (drag to Done, rework, or discard) |
| Done | `done` | nobody (terminal) |
| (hidden) | `discarded` | nobody (terminal) |

In Building, the card shows its sub-stage, its round and the reviewer chips. Only Backlog,
Approved, Ready for review and Done accept drops.

## Targets

Every idea names a **target**: the absolute path of the git repo the work lands in. The human
types it next to the idea; nothing defaults it. The factory itself (this repo's `factory/`,
`.claude/`, `.factory-runs/` and dashboard) never moves; only the code it builds goes to the target.

- An existing git repo: jobs branch from its `main` and merge back into it. The idea goes straight
  to the spec-writer in the same session.
- A **new** target (a path that does not exist yet, or an empty folder) needs **scaffold options**,
  chosen by the human in the idea form. `new-idea` then creates a **bootstrap job** instead of
  starting the spec-writer: its spec, generated from `factory/templates/bootstrap.md`, scaffolds
  the project from the `template-projects` templates. The idea waits (`waiting`) until that job is
  merged; the merge releases it (`drafting`), and the human resumes it with `/factory:idea I-NNN`,
  so the spec-writer specs features against real code.
  The first `checkout` creates the target with `git init -b main` and an empty initial commit;
  the control plane does this, never an agent.
- Anything else (a non-empty folder that is not a git repo, a relative path, scaffold options for an
  existing repo, a new target without them) is refused by `new-idea`, and the same target checks
  by `approve`.

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
Agents may read it and never edit it.

Each target has one sibling worktree, `<target>.wt`, reused by every job on that target. One job
builds at a time across all targets. A job with no `target` (created before targets existed) uses
this repo, as before.

The spec carries the target and the gate, and the human can edit both in Backlog:

```
## Target
- Repo: D:\code\Scratchpad\todo-app

## Gate
- build: `dotnet build`
- test: `dotnet test`
```

Gate lines are ``- <name>: `<command>` ``, where `<name>` is lower-case letters, digits or `-`. The
commands run with `pwsh -Command` in the worktree. The human approves them along with the spec, and
no agent can edit `jobs/<id>/spec.md`.

A dependency install in a gate is guarded, so the gate does not reinstall on every run. For npm,
that means `npm ci` runs only when `node_modules/.package-lock.json` (which npm writes at the end
of an install) is missing or older than `package-lock.json`:

```
- ui: `Set-Location src/ui/react; if (-not (Test-Path node_modules/.package-lock.json) -or (Get-Item package-lock.json).LastWriteTime -gt (Get-Item node_modules/.package-lock.json).LastWriteTime) { npm ci }; npm run build`
```

## Ideas

An idea is worked out in an interactive Claude Code session, `/factory:idea [<target> | I-NNN]`
(`factory/scripts/idea.ps1` starts one with the plugin loaded). It is not a job. The session:

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
board.ideas[] = { "id": "I-001", "text": "...", "target": "D:\\code\\Scratchpad\\todo-app",
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
| ready | done | human `merge`; see [Gate and merge](#gate-and-merge). A bootstrap job's merge moves its idea `waiting` → `drafting` and prints `released <ideaId>` | |
| ready | ready | human `merge` refused: dirty checkout or merge conflict / red gate | `merge-blocked` / `gate-failed` |
| ready | approved | human `rework <id> <note>`; writes `jobs/<id>/rework-<n>.md`, round += 1, sets `approvedAt` | |
| ready | discarded | human; deletes branch `factory/<id>` | |

`done` and `discarded` are terminal. `flag` is cleared on every move out of `ready`.

The round cap applies to automated loops only. A reworked job gets one build and one review pass
at its new round. If the reviewers still return `CHANGES`, it comes back to Ready for review
flagged `max-rounds`.

Approval of a spec, the approver's `APPROVE`, a green gate and a completed merge are four separate
facts. The board records each one on its own. Only a human drag to Done merges.

### Purge

`factory.ps1 purge` clears finished work off the board. It removes every job in `done` or
`discarded`, and every idea in `done` or `failed` none of whose jobs is left. For each it deletes
`jobs/<id>/` or `ideas/<id>/`, and for a job also the branch `factory/<id>` in its target (detaching
`<target>.wt` first if it is on that branch). It appends one `note` event (`purged J-001, ...`) and
prints the removed ids, one per line. Ideas in `waiting` or `drafting` and jobs in any other stage
are untouched. `events.jsonl` keeps its history, and `board.seq` stops a purged id being reused.

## Files

```
.factory-runs/                 gitignored; FACTORY_HOME overrides its location
  board.json
  events.jsonl
  runner.lock                  held while runner.ps1 is draining the queue
  headless-settings.json       written by runner.ps1 for each claude -p run (see Runner)
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

<target>.wt\                   one worktree per target, reused by every job on it (legacy jobs: ..\claude-factory.wt)

factory/plugin/                the agents, the /factory, /factory:idea and grill skills and the Subagent
                               hooks, as a Claude Code plugin loaded with --plugin-dir, so they work in
                               any target's worktree
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
      "target": "D:\\code\\Scratchpad\\todo-app",
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
  `ready` job carries one.
- `merged` is `null` until a merge, then `{ "sha": "<merge commit>", "repo": "<target>", "added": ["<absolute path>", ...], "modified": [...] }`,
  from `git diff --name-status --no-renames <sha>^1 <sha>` in the target.
- `target` is absent on legacy jobs, which use this repo.
- `kind` is `feature` (from a spec-writer spec) or `bootstrap` (generated by `new-idea`); absent means `feature`.
- `costUsd` and `durationMs` are summed over every runner pass on the job.
- Ids are `I-NNN` and `J-NNN`, allocated by the script, one above the larger of `seq` and the highest
  id on the board. `seq` records the highest id ever allocated, so a `purge` never frees an id. The simulator uses the same ids in its own
  state dir (`.factory-runs-sim/`), with a throwaway sandbox repo at `.factory-runs-sim/repo`.

## `events.jsonl`

One JSON object per line, append-only. The dashboard builds its timeline and its "agent running
now" pulses from this file.

```json
{"ts":"2026-10-12T13:55:00Z","idea":"I-001","kind":"idea","detail":"drafting"}
{"ts":"2026-10-12T14:02:10Z","job":"J-001","kind":"agent-start","agent":"security-reviewer","agentId":"a2f5009","transcript":"C:\\Users\\me\\.claude\\projects\\x\\0089\\subagents\\agent-a2f5009.jsonl","round":1}
{"ts":"2026-10-12T14:04:55Z","job":"J-001","kind":"agent-stop","agent":"security-reviewer","round":1}
{"ts":"2026-10-12T14:05:00Z","job":"J-001","kind":"stage","from":"review","to":"approve","by":"orchestrator"}
{"ts":"2026-10-12T14:07:30Z","job":"J-001","kind":"gate","detail":"build=0 test=0 ui=0 sha=3f2a1c9"}
{"ts":"2026-10-12T14:08:00Z","job":"J-001","kind":"cost","costUsd":1.84,"durationMs":412000}
```

`kind` is one of `idea | stage | agent-start | agent-stop | gate | merge | cost | note`. Spec-writer
agent events carry `"idea"` instead of `"job"`. `agent` is the plain agent name (the hook strips the
plugin's `factory:` prefix). `agent-start` carries `agentId` and `transcript`, the subagent's live
Claude Code transcript, which is derived from the hook's `transcript_path` and `agent_id`.

## `factory.ps1` commands

State resolves from `$env:FACTORY_HOME`, else `<main checkout>/.factory-runs` (found through
`git rev-parse --git-common-dir`, so it works from inside the worktree). Writes to `board.json` are
atomic (a temp file, then a rename). A refused command exits non-zero with a one-line reason on
stderr and leaves the board unchanged.

| Command | Used by |
| --- | --- |
| `new-idea <text> -Target <path> [-Scaffold <json>]` · `add-spec <ideaId> <file>` · `idea-done <ideaId> [-Failed -Reason]` | `/factory:idea` |
| `edit-spec <id> -File <path>` (backlog only) | serve.ps1 |
| `approve <id>` | `/factory:idea` (on the human's yes), serve.ps1 (human drag) |
| `rework <id> <note>` · `discard <id>` · `merge <id>` · `purge` | serve.ps1 (human drags and **Clear**) |
| `next` (prints the next runnable approved id, or nothing) · `checkout <id>` (creates the target repo if needed, then creates or reuses `<target>.wt` on `factory/<id>`; prints the worktree path) · `set-cost <id> <usd> <ms>` | runner.ps1 |
| `set-stage <id> <to> [-By] [-Reason] [-Detail]` · `set-review <id> <role> <state>` · `read-reviews <id>` · `read-decision <id>` · `gate <id>` | `/factory run` |
| `event <json \| -Job -Idea -Kind -Agent>` | hook-event.ps1 |
| `status [-Json]` | anyone |

`read-reviews` and `read-decision` exit 2 when a verdict line is missing or malformed; the job
does not move. `set-stage` only performs `approved → build`, `build → review` and
`Building → ready -Reason escalated`; every other transition has its own command.

Overrides, for tests: `$env:FACTORY_GATE_CMDS` (JSON `[{ "name", "cmd" }]`; wins over the spec's
`## Gate`, so tests can use `exit 0` / `exit 1`), `$env:FACTORY_WORKTREE` (the worktree path, for
every target) and `$env:FACTORY_REPO` (this repo's main checkout).

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

Every agent works in the target's worktree and follows the target's `CLAUDE.md`, if it has one.
Reviewers inspect the diff (`git diff main...HEAD`) and re-run the spec's Gate commands themselves.
They treat `build.md`'s claims as claims, not as evidence.

## Gate and merge

`factory.ps1 gate <id>` runs the gate commands in the target's worktree on `factory/<id>`, in order.
It writes the output to `gate.log` and records the exit codes and the branch head `sha`. The
commands come from `$env:FACTORY_GATE_CMDS` if set, else from the spec's `## Gate`. Legacy jobs with
no target use this repo's defaults: `dotnet build shelf.slnx`, `dotnet test tests/shelf.api.tests`,
and `npm run build` in `src/ui/react`.

`factory.ps1 merge <id>` runs in the target's main checkout:

0. It refuses a branch with no commits beyond the target's `main` (`merge-blocked`, "nothing to merge").
1. If no all-zero gate is recorded for the current head sha of `factory/<id>`, it runs `gate`
   first. A red gate leaves the job in `ready` with flag `gate-failed`. A human can overrule a
   reviewer flag by dragging to Done, but cannot skip the gate.
2. It requires the main checkout to be on `main` and clean **by content**: `git diff --quiet HEAD`
   (staged or unstaged changes to tracked files). A file that differs only in line endings or stat
   data is not dirty. Untracked files are left to `git merge`, which refuses to overwrite one. On
   refusal the flag detail names the dirty paths. A file `git status` still lists as modified after
   that check differs only in line endings or stat data. It is restored from `HEAD`
   (`git checkout -- <path>`) so that git's own overwrite check does not refuse the merge. The content is unchanged.
3. It runs `git merge --no-ff factory/<id>`. On a conflict it aborts the merge, and the detail is
   `merge conflict: <paths>`. Any other refusal by git puts git's message in the detail.

A failure at step 2 or 3 leaves the job in `ready` with flag `merge-blocked`. Success moves it to
`done`, records `merged`, and appends a `merge` event.

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
| `POST /api/jobs/<id>/move` `{ "to": "approved", "note" }` from ready | `rework` | starts the runner |
| `POST /api/jobs/<id>/move` `{ "to": "done" }` | `merge` | |
| `POST /api/jobs/<id>/move` `{ "to": "discarded" }` | `discard` | |
| `POST /api/purge` | `purge` | |

There is no idea endpoint: ideas are created in the `/factory:idea` session. An idea card shows the
command that resumes it, and its modal shows `brief.md`.

"Starts the runner" means: spawn `runner.ps1` detached, unless `runner.lock` is held. The
[watchdog](#runner) also starts it when Approved is not empty and no live runner holds the lock. The claude
command is taken from `$env:FACTORY_CLAUDE_CMD` when it is set, so tests spend no tokens.

The job modal's **Output** tab tails `run-<n>.jsonl` (the orchestrator) and each agent's
`transcript`. It shows narration, tool calls and results. Claude Code writes thinking blocks with
empty text, so a thinking block shows only as a marker.

## Runner

`runner.ps1` drains the Approved queue one job at a time. It exits at once if it cannot create
`runner.lock` (an exclusive file create holding its PID). It takes over a lock whose PID is no
longer running. Otherwise it loops:

1. `factory.ps1 next`. If nothing comes back, it deletes the lock and exits.
2. It calls `factory.ps1 set-stage <id> build -By runner`, then `factory.ps1 checkout <id>`, which
   creates the target repo if needed and creates or reuses `<target>.wt` on `factory/<id>` (an
   existing branch when the job is reworked, otherwise a new one from `main`).
3. It sets `FACTORY_JOB=<id>`, `FACTORY_HOME=<this repo>\.factory-runs` and `FACTORY_REPO=<this repo>`,
   writes `<stateDir>/headless-settings.json`, and runs this in the target's worktree:
   `claude -p "/factory:factory run <id> <stateDir> <factory.ps1>" --plugin-dir <this repo>/factory/plugin --settings <stateDir>/headless-settings.json --add-dir <stateDir> --output-format stream-json --verbose --permission-mode acceptEdits`
   (`claude-run.ps1` builds this command for both scripts).
   - `headless-settings.json` `permissions.allow` lets agents run the toolchain (`dotnet`, `npm`,
     `npx`, `node`), git reads and commits, and read-only shell commands, in both the Bash and the
     PowerShell tool; `deny` blocks `git push`, `merge`, `rebase`, `reset --hard`, switching to
     `main` and `git worktree`. `additionalDirectories` makes `factory/templates` and the template
     workspace readable. The allowlist also holds the absolute
     `factory.ps1` path. The skill calls `factory.ps1` by that absolute path with no
     `$VARIABLES`, because a headless permission check can't approve a variable it can't see.
   - `permissions.deny` is the fence: `Edit` on `/factory/**`, `/.claude/**` and
     `/docs/FACTORY-CONTRACT.md` in the worktree, the same three by absolute path in this repo,
     `<stateDir>/jobs/*/spec.md`, and the template workspace.
   - claude's output is read as UTF-8 whatever the console's codepage.
   - stderr goes to the same log, so a reader skips lines that are not JSON.
4. It appends each stdout line to `jobs/<id>/run-<n>.jsonl` as it arrives, and calls `set-cost`
   with `total_cost_usd` and `duration_ms` from the final `"type":"result"` line. If the job is
   in `gate` afterwards, the approver has approved and only the gate is left, so the runner runs
   `factory.ps1 gate <id>` itself. A claude that dies at that point (out of credit, say) costs
   nothing. If the job is still in Building after that, whether claude exited non-zero or not, it
   moves the job to `ready` with flag `escalated`. That way the queue never stalls.
5. An error while handling one job does not end the runner. It logs the error, moves the job to
   `ready` with flag `escalated` if it is still in Building (detail `runner error: <message>`), and
   goes back to step 1.
6. It appends to `<stateDir>/runner.log`, one timestamped line each: `start pid=<pid>`,
   `take <id>`, `claude <id> exit=<code> result=<yes|no>`, `stage <id> <stage>`,
   `error <id>: <message>` followed by the stack, and how it ended: `exit drained`, `exit fatal: <message>`,
   or `exit stopped` (interrupted, e.g. its console closed, before the loop finished).

**Watchdog.** `serve.ps1` checks the board every 10 seconds (`-WatchdogSeconds`). If a job is in
`approved` and `runner.lock` is missing or names a PID that is no longer running, it starts
`runner.ps1` detached and logs `watchdog: started runner`. A runner that died, or one that was
never started (an approval made outside the dashboard), costs one interval, not a stalled queue.

The `/factory:idea` session doesn't use the runner or the lock. It writes only under `ideas/` and
through `factory.ps1`, so it can run while a job builds. After it approves a spec it starts
`runner.ps1` detached, as serve.ps1 does.

# Factory contract

This file is what the factory's moving parts agree on: the dashboard, `serve.ps1`, `runner.ps1`,
the orchestrator skill, the agents, `factory.ps1` and the hooks. When any of them disagrees with
this file, this file wins, and the disagreement is a bug in the other one. The data formats can
change, but only by editing this file first.

## Principle

Agents propose and execute; `factory.ps1` attests and constrains; a human owns intent and the
release. A human touches the factory at exactly two points: **approving a spec** (Backlog →
Approved) and **accepting the work** (Ready for review → Done). Everything between those two is
automated.

No agent, skill or endpoint writes to `.factory-runs/board.json`. Every state change is a
`factory.ps1` call, and the script refuses the ones this contract does not allow.

## The flow

```
 Ideas ──spec-writer──→ Backlog ──human drag──→ Approved ──runner──→ Building ──→ Ready for review ──human drag──→ Done
 (I-NNN)   1..N cards   (edit spec)              (FIFO queue)        build         (flag if the       (merge)
                           │                          ↑              review ×4      factory gave up)
                           └──discard                 │              approve            │
                                                      │              gate               ├──discard
                                                      └────────── rework <note> ────────┘
```

## Board columns and stages

| Column | Stages | Who moves cards out |
| --- | --- | --- |
| Ideas | `idea` records with status `drafting` or `failed` (not jobs) | spec-writer |
| Backlog | `backlog` | human (drag to Approved, or discard) |
| Approved | `approved` | runner |
| Building | `build`, `review`, `approve`, `gate` | orchestrator / script |
| Ready for review | `ready` | human (drag to Done, rework, or discard) |
| Done | `done` | nobody (terminal) |
| (hidden) | `discarded` | nobody (terminal) |

In Building, the card shows its sub-stage, its round and the reviewer chips. Only Backlog,
Approved, Ready for review and Done accept drops.

## Ideas

An idea is the text a human types into the dashboard. It is not a job. The spec-writer turns it
into 1..N specs (N ≤ 5). Each spec is small and buildable on its own, and each one becomes a job
in `backlog`.

```
board.ideas[] = { "id": "I-001", "text": "...", "status": "drafting|done|failed",
                  "jobs": ["J-004", "J-005"], "createdAt": "..." }
```

- `factory.ps1 new-idea <text>` allocates `I-NNN`, writes `ideas/I-NNN/idea.md` and sets status `drafting`.
- The spec-writer writes `ideas/I-NNN/spec-1.md .. spec-N.md`, following `factory/templates/spec.md`.
- `factory.ps1 add-spec <ideaId> <file>` allocates `J-NNN`, copies the file to `jobs/J-NNN/spec.md`,
  takes the title from the file's `# Spec:` heading, and creates the job in `backlog`.
- `factory.ps1 idea-done <ideaId> [-Failed -Reason <text>]` closes the idea. Ideas in `done`
  are no longer shown; ideas in `failed` stay in Ideas with the reason.

## Job transitions

`by` is `human`, `runner`, `orchestrator` or `gate`.

| From | To | Allowed when | `flag.reason` |
| --- | --- | --- | --- |
| backlog | approved | human; `spec.md` exists. Sets `approvedAt` | |
| backlog | discarded | human | |
| approved | build | runner; no job is in `build`, `review`, `approve` or `gate`, and this is the oldest `approvedAt` | |
| build | review | `round-<n>/build.md` exists | |
| review | build | all 4 verdicts in, at least one `CHANGES`, round < 2; round += 1, reviews reset to `pending` | |
| review | ready | at least one `CHANGES` at round ≥ 2 | `max-rounds` |
| review | approve | all 4 verdicts are `PASS` | |
| approve | gate | `decision.md` first line is `Decision: APPROVE` | |
| approve | ready | `Decision: ESCALATE` | `escalated` |
| gate | ready | every gate command exited 0 (no flag), or any one did not | `gate-failed` |
| build / review / approve / gate | ready | the orchestrator gives up, or the runner's `claude` process exits non-zero | `escalated` |
| ready | done | human `merge`; see [Gate and merge](#gate-and-merge) | |
| ready | ready | human `merge` refused: dirty checkout or merge conflict / red gate | `merge-blocked` / `gate-failed` |
| ready | approved | human `rework <id> <note>`; writes `jobs/<id>/rework-<n>.md`, round += 1, sets `approvedAt` | |
| ready | discarded | human; deletes branch `factory/<id>` | |

`done` and `discarded` are terminal. `flag` is cleared on every move out of `ready`.

The round cap applies to automated loops only. A reworked job gets one build and one review pass
at its new round. If the reviewers still return `CHANGES`, it comes back to Ready for review
flagged `max-rounds`.

Approval of a spec, the approver's `APPROVE`, a green gate and a completed merge are four separate
facts. The board records each one on its own. Only a human drag to Done merges.

## Files

```
.factory-runs/                 gitignored; FACTORY_HOME overrides its location
  board.json
  events.jsonl
  runner.lock                  held while runner.ps1 is draining the queue
  ideas/<id>/
    idea.md                    the idea text, verbatim
    spec-<k>.md                spec-writer output, one per proposed job
    run.json                   the claude -p result of the spec run
  jobs/<id>/
    spec.md                    copied from the idea, then edited by the human in Backlog
    rework-<n>.md              the human's rework note for round n
    round-<n>/
      build.md
      review-security.md  review-code.md  review-ux.md  review-ui.md
    decision.md
    gate.log
    run-<n>.json               the claude -p result for each runner pass

..\claude-factory.wt\          the one factory worktree, reused by every job (outside the repo)
```

Each review's first line must be exactly `Verdict: PASS` or `Verdict: CHANGES`. The decision's
first line must be exactly `Decision: APPROVE` or `Decision: ESCALATE`. `factory.ps1` parses
those lines and nothing else; if one is missing or malformed, the script treats it as no verdict.

## `board.json`

```json
{
  "version": 2,
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
      "reviews": { "security": "running", "code": "PASS", "ux": "pending", "ui": "CHANGES" },
      "decision": null,
      "gate": null,
      "flag": null,
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
- `costUsd` and `durationMs` are summed over every runner pass on the job.
- Ids are `I-NNN` and `J-NNN`, allocated by the script. The simulator uses the same ids in its own
  state dir (`.factory-runs-sim/`), with a throwaway sandbox repo at `.factory-runs-sim/repo`.

## `events.jsonl`

One JSON object per line, append-only. The dashboard builds its timeline and its "agent running
now" pulses from this file.

```json
{"ts":"2026-10-12T13:55:00Z","idea":"I-001","kind":"idea","detail":"drafting"}
{"ts":"2026-10-12T14:02:10Z","job":"J-001","kind":"agent-start","agent":"security-reviewer","round":1}
{"ts":"2026-10-12T14:04:55Z","job":"J-001","kind":"agent-stop","agent":"security-reviewer","round":1}
{"ts":"2026-10-12T14:05:00Z","job":"J-001","kind":"stage","from":"review","to":"approve","by":"orchestrator"}
{"ts":"2026-10-12T14:07:30Z","job":"J-001","kind":"gate","detail":"build=0 test=0 ui=0 sha=3f2a1c9"}
{"ts":"2026-10-12T14:08:00Z","job":"J-001","kind":"cost","costUsd":1.84,"durationMs":412000}
```

`kind` is one of `idea | stage | agent-start | agent-stop | gate | merge | cost | note`. Spec-writer
agent events carry `"idea"` instead of `"job"`.

## `factory.ps1` commands

State resolves from `$env:FACTORY_HOME`, else `<main checkout>/.factory-runs` (found through
`git rev-parse --git-common-dir`, so it works from inside the worktree). Writes to `board.json` are
atomic (a temp file, then a rename). A refused command exits non-zero with a one-line reason on
stderr and leaves the board unchanged.

| Command | Used by |
| --- | --- |
| `new-idea <text>` · `add-spec <ideaId> <file>` · `idea-done <ideaId> [-Failed -Reason]` | serve.ps1, `/factory spec` |
| `edit-spec <id> -File <path>` (backlog only) | serve.ps1 |
| `approve <id>` · `rework <id> <note>` · `discard <id>` · `merge <id>` | serve.ps1 (human drags) |
| `next` (prints the next runnable approved id, or nothing) · `checkout <id>` (creates or reuses the worktree on `factory/<id>`) · `set-cost <id> <usd> <ms>` | runner.ps1 |
| `set-stage <id> <to> [-By] [-Reason] [-Detail]` · `set-review <id> <role> <state>` · `read-reviews <id>` · `read-decision <id>` · `gate <id>` | `/factory run` |
| `event <json \| -Job -Idea -Kind -Agent>` | hook-event.ps1 |
| `status [-Json]` | anyone |

`read-reviews` and `read-decision` exit 2 when a verdict line is missing or malformed; the job
does not move. `set-stage` only performs `approved → build`, `build → review` and
`Building → ready -Reason escalated`; every other transition has its own command.

Overrides, for tests: `$env:FACTORY_GATE_CMDS` (JSON `[{ "name", "cmd" }]`, so tests can use
`exit 0` / `exit 1` instead of dotnet), `$env:FACTORY_WORKTREE` (the worktree path) and
`$env:FACTORY_REPO` (the main checkout).

## Agents

| Agent | Model | Reads | Writes |
| --- | --- | --- | --- |
| spec-writer | opus | idea.md, the repo | ideas/<id>/spec-1.md .. spec-N.md (N ≤ 5) |
| builder | opus | spec.md, previous round's reviews, rework note | source on the job branch, round-n/build.md |
| security-reviewer | opus | spec.md, the diff, build.md | round-n/review-security.md |
| code-reviewer | sonnet | same | round-n/review-code.md |
| ux-reviewer | sonnet | same | round-n/review-ux.md |
| ui-reviewer | sonnet | same + docs/DESIGN.md | round-n/review-ui.md |
| approver | opus | spec.md, every build.md and review in the job | decision.md |

Reviewers inspect the diff (`git diff main...factory/<id>`) and re-run the tests themselves. They
treat `build.md`'s claims as claims, not as evidence.

## Gate and merge

`factory.ps1 gate <id>` runs these in the worktree on `factory/<id>`, in this order. It writes the
output to `gate.log` and records the exit codes and the branch head `sha`:

1. `dotnet build shelf.slnx`
2. `dotnet test tests/shelf.api.tests`
3. `npm run build` in `src/ui/react`

`factory.ps1 merge <id>` runs in the main checkout:

1. If no all-zero gate is recorded for the current head sha of `factory/<id>`, it runs `gate`
   first. A red gate leaves the job in `ready` with flag `gate-failed`. A human can overrule a
   reviewer flag by dragging to Done, but cannot skip the gate.
2. It requires the main checkout to be on `main` and clean (`git status --porcelain` empty).
3. It runs `git merge --no-ff factory/<id>`. On a conflict it aborts the merge.

A failure at step 2 or 3 leaves the job in `ready` with flag `merge-blocked`. Success moves it to
`done` and appends a `merge` event.

## Dashboard API

`serve.ps1` listens on localhost only. GETs serve `factory/dashboard/index.html` and read-only
files under the state dir: it rejects `..`, and serves `.md` as text/plain. Every write is a thin
wrapper over one `factory.ps1` call. The response is `{ "ok": bool, "message": "<stderr or stdout>" }`,
and a refused move snaps the card back and shows the message.

| Request | Calls | Then |
| --- | --- | --- |
| `GET /api/jobs/<id>/files` | (read-only) | the job's artifact paths, relative to `jobs/<id>/` |
| `POST /api/ideas` `{ "text" }` | `new-idea` | starts `spec-run.ps1 I-NNN` detached (see below) |
| `PUT /api/jobs/<id>/spec` (text/markdown) | `edit-spec` | |
| `POST /api/jobs/<id>/move` `{ "to": "approved" }` from backlog | `approve` | starts the runner |
| `POST /api/jobs/<id>/move` `{ "to": "approved", "note" }` from ready | `rework` | starts the runner |
| `POST /api/jobs/<id>/move` `{ "to": "done" }` | `merge` | |
| `POST /api/jobs/<id>/move` `{ "to": "discarded" }` | `discard` | |

"Starts the runner" means: spawn `runner.ps1` detached, unless `runner.lock` is held. The claude
command is taken from `$env:FACTORY_CLAUDE_CMD` when it is set, so tests spend no tokens.

## Runner

`runner.ps1` drains the Approved queue one job at a time. It exits at once if it cannot create
`runner.lock` (an exclusive file create holding its PID). It takes over a lock whose PID is no
longer running. Otherwise it loops:

1. `factory.ps1 next`. If nothing comes back, it deletes the lock and exits.
2. It creates `..\claude-factory.wt` if it doesn't exist, or reuses it. It then checks out
   `factory/<id>`: an existing branch when the job is reworked, otherwise a new one from `main`.
3. It calls `factory.ps1 set-stage <id> build -By runner`, sets `FACTORY_JOB=<id>` and
   `FACTORY_HOME=<main checkout>\.factory-runs`, and runs
   `claude -p "/factory run <id>" --output-format json` in the worktree.
4. It saves the result to `jobs/<id>/run-<n>.json` and calls `set-cost` with `total_cost_usd` and
   `duration_ms`. If the job is still in Building afterwards, whether claude exited non-zero or
   not, it moves the job to `ready` with flag `escalated`. That way the queue never stalls.

The spec run for an idea doesn't use the runner or the lock. It reads the repo and writes only
under `ideas/`, so it can run while a job builds. `spec-run.ps1 <ideaId>` sets `FACTORY_IDEA`, runs
`claude -p "/factory spec <ideaId>" --output-format json` in the main checkout (or
`FACTORY_CLAUDE_CMD`), and saves `ideas/<id>/run.json`. If the idea is still `drafting` afterwards,
it marks the idea `failed`.

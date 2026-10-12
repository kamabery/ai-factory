# Build guide: finishing the factory

What already exists: Shelf is scaffolded and green (22 API tests, the AppHost build and the React
build all pass), the contract is in `docs/FACTORY-CONTRACT.md`, the seed backlog and artifact
templates are in `factory/`, and the empty folders are in place. Everything below is still to do.

Each step gives a prompt to paste into Claude Code in `D:\code\claude-factory`, the test that has to
be **seen red before it goes green**, and the evidence that closes the step. Do the steps in order,
committing each one on `main` once its evidence is in. Steps 1–4 build the factory, steps 5–6 prove
it on real work, and step 7 makes it parallel.

Keep a scratch log of the wall-clock time and the token or cost figures for steps 5–7. Those
numbers are half of the "validated" bar.

---

## Step 1: Control plane (`factory.ps1`), test-first with Pester

**Prompt:**

> Read docs/FACTORY-CONTRACT.md. Using TDD with Pester 6, build `factory/scripts/factory.ps1`: a single
> script with subcommands `new-job <title> [-RequestFile]`, `set-stage <id> <to> [-By] [-Reason] [-Detail]`,
> `set-review <id> <role> <pending|running|PASS|CHANGES>`, `read-reviews <id>` (parses the 4 verdict
> first-lines of the current round into the board and moves the job review→build / review→approve /
> review→needs-human per the contract), `read-decision <id>`, `event <json|-Job -Kind -Agent>`,
> `gate <id>`, `merge <id>`, `status [-Json]`, `approve <id>`, `rework <id> <note>`, `active <id>`.
> It resolves state from `$env:FACTORY_HOME`, else `<repo root>/.factory-runs`. Writes to board.json
> are atomic: write a temp file, then rename. Write `factory/tests/factory.Tests.ps1` FIRST, and
> run it and show me it failing before you write the script. Tests use a temp git repo fixture,
> never this repo's state. The gate commands must be injectable (`$env:FACTORY_GATE_CMDS` as JSON)
> so the tests can use `exit 0` and `exit 1` instead of dotnet.

**The tests must cover at least the following:**
- `new-job` allocates `J-001`, then `J-002`, creates `jobs/<id>/request.md`, and puts the job in `backlog`.
- An illegal transition (`spec → merged`) throws, and the board is unchanged.
- Four `PASS` verdicts move a job `review → approve`; one `CHANGES` at round 1 moves it to `build` round 2,
  with every review reset to `pending`; `CHANGES` at round 2 lands it in `needs-human/max-rounds`.
- A missing or malformed verdict line counts as no verdict, and the job doesn't move.
- `Decision: ESCALATE` → `needs-human/escalated`.
- `gate` with a failing command → `needs-human/gate-failed`, with the exit codes on `gate` and the output in `gate.log`.
- `merge` on a dirty fixture tree → `needs-human/merge-blocked`, and nothing gets merged.
- `merge` on a clean tree → a `--no-ff` merge commit on `main` and the job in `merged`.
- `approve` from needs-human → gate; `rework` → build with round + 1 and the note in `jobs/<id>/rework-<n>.md`.
- Every transition appends a `stage` event to `events.jsonl`.

**Evidence:** red output, then `Invoke-Pester factory/tests` green.

---

## Step 2: Dashboard and simulator

**Prompt:**

> Build the factory dashboard per docs/FACTORY-CONTRACT.md, test first.
> 1. `factory/scripts/simulate.ps1 [-Speed 1.0] [-Reset]`: using ONLY factory.ps1 calls, with
>    `$env:FACTORY_HOME` pointed at `.factory-runs-sim/` (gitignore it), it drives `SIM-001` through
>    spec → build → review (agent-start/stop events for all 4 reviewers in parallel) → approve → gate → merged,
>    and `SIM-002` through a CHANGES round to `needs-human/max-rounds`, and `SIM-003` to `needs-human/merge-blocked`.
>    The gate and merge use injected commands; the simulator never touches git or dotnet.
> 2. `factory/scripts/serve.ps1 [-Port 4317] [-StateDir]`: an HttpListener on localhost only. It serves
>    `factory/dashboard/index.html` and read-only GETs under the state dir. Reject `..` path traversal,
>    and serve `.md` as text/plain.
> 3. A Playwright test in `factory/tests/dashboard/` (an `npx playwright test` project with its own
>    package.json). It starts serve.ps1 against `.factory-runs-sim`, runs simulate.ps1 at high speed,
>    and asserts that SIM-001's card ends in the Merged column, SIM-002 and SIM-003 end in Needs human
>    with their reason codes visible, the reviewer chips show PASS/CHANGES, and opening SIM-001's
>    drawer shows a timeline with ≥ 10 events and a working link to spec.md. Run it and show it failing.
> 4. Then build `factory/dashboard/index.html` (one file, no build step, no CDN dependencies) until it passes:
>    - a pipeline strip that redraws the video's graph: spec-writer → builder → [security, code, ux, ui]
>      → approver → gate → merge. A node pulses while an agent-start has no matching agent-stop, and
>      shows a badge for each job id it's working on.
>    - a Kanban board with columns Backlog · Spec · Build · Review · Approve · Gate · Merged · Needs human.
>      Cards animate between columns (a FLIP transition) and show the id, title, round, reviewer chips,
>      elapsed time, and cost when present.
>    - a job drawer with links to the artifacts (fetched and shown as preformatted text), the timeline,
>      the gate exit codes, the needs-human reason + detail, and a copy button for the recovery command
>      (`/factory approve <id>` or `/factory rework <id> "<note>"`).
>    - polling of board.json and events.jsonl every 1.5 s, with light and dark themes.

**Evidence:** the Playwright test red, then green. Then **watch it by eye**: run `serve.ps1` and
`simulate.ps1 -Speed 0.5`, open `http://localhost:4317`, and confirm the cards visibly travel the
board and the pipeline nodes light up. This is the "things running through the factory" view.

---

## Step 3: Agents

**Prompt:**

> Write the 7 agents in `.claude/agents/` per the Agents table in docs/FACTORY-CONTRACT.md. Each one gets
> frontmatter with `name`, `description`, `model` (opus/sonnet as in the table) and a minimal
> `tools` list. Reviewers and the approver get Read, Grep, Glob and Bash (for git diff and running tests)
> plus Write, and may write only their own output file. The builder gets the full edit tools.
> Each prompt states its exact input files, its exact output path, and that the output must follow
> `factory/templates/<kind>.md`, verdict line first. Reviewer prompts include:
> "Return CHANGES only for a blocking defect you can point to at file:line, with a fix; everything else is a Note."
> (This is the counter to the over-escalation seen in the video.) The security reviewer checks
> secrets/PII in logs, authz on new endpoints, input validation and injection. The code reviewer checks
> correctness, tests that actually assert the ACs, and the contract regeneration rules in CLAUDE.md. The UX
> reviewer checks behaviour against the spec's ACs from a user's point of view. The UI reviewer checks
> docs/DESIGN.md compliance (no raw values, components from src/components/ui). The approver decides
> APPROVE only if every AC is evidenced and every review is PASS; otherwise ESCALATE with the single
> decision a human must make.

**Test:** this step has no executable test of its own. Its proof is step 5. Before moving on,
review the seven files yourself: this is where the factory's quality bar lives.

---

## Step 4: Orchestrator skill and hooks

**Prompt:**

> Write `.claude/skills/factory/SKILL.md`, the /factory orchestrator. Commands:
> `/factory <feature text>` (new job from text), `/factory next` (first backlog.md heading with no job
> of the same title on the board), `/factory run <id>`, `/factory status`, `/factory approve <id>`,
> `/factory rework <id> <note>`. Rules: the orchestrator stays thin. It never writes code or artifacts
> itself; every state change is a factory.ps1 call; it checks that the release checkout is clean and on main
> before creating a job branch; it calls `factory.ps1 active <id>`; it runs the spec-writer, then
> the builder on branch `factory/<id>`, then the 4 reviewers as 4 parallel Agent calls in ONE message,
> then `read-reviews`, looping per the contract; then the approver, `read-decision`, `gate`, and `merge`.
> When the script puts a job in needs-human, it stops and prints the reason and the recovery command.
>
> Then add `.claude/settings.json` hooks: SubagentStart and SubagentStop call
> `pwsh -NoProfile -File factory/scripts/hook-event.ps1`, which reads the hook JSON from stdin and appends
> agent-start/agent-stop for the job in `$env:FACTORY_JOB`, else `.factory-runs/active-job`. It exits 0
> silently when there's no active job, so hooks never break a normal session.
> First confirm the current SubagentStart/SubagentStop hook input fields (agent type/name)
> from the Claude Code hooks documentation; don't assume them. Write Pester tests for hook-event.ps1
> using captured sample hook payloads, and see them fail first.

**Test:** the Pester tests for `hook-event.ps1` go red, then green. Then a smoke run: `/factory status`
on an empty board prints an empty board without error.

---

## Step 5: First real job, end to end

1. Terminal A: `pwsh factory/scripts/serve.ps1`, then open `http://localhost:4317`.
2. Terminal B: `claude`, then `/factory next` (it takes "Book search filter").
3. Watch the card cross every column. Answer permission prompts as they come; leave bypass mode off
   for the first run.

**Pass criteria (check each one; don't trust the summary):**
- The card ends in **Merged**, and the timeline shows all 7 agents starting and stopping.
- `.factory-runs/jobs/J-001/` has spec.md, round-1/build.md, the 4 reviews, decision.md and gate.log.
- The gate exit codes are all 0, `git log --graph --oneline -5` shows the `--no-ff` merge of `factory/J-001`,
  and `git status` on main is clean.
- `dotnet test tests/shelf.api.tests` and `npm run build` are green on main.
- `aspire run`, then `/books`: the filter actually works. This hands-on check is the one that counts.

---

## Step 6: Failure paths (the video's lessons, as tests)

Run each one and confirm the end state on the dashboard and in `board.json`.

| Case | How | Expected |
| --- | --- | --- |
| 6a Dirty tree at merge | Run `/factory next` ("Author on Book"). While it's in review, `echo x >> README.md` on main | `needs-human / merge-blocked`. Then `git checkout README.md` and `/factory approve J-002` → `merged` |
| 6b Security rejection | `/factory Log every incoming request including all headers to the console` | The security reviewer returns CHANGES (auth headers / PII) → round 2 → either fixed (headers redacted) or `needs-human / max-rounds` |
| 6c Human rework | On any job at needs-human: `/factory rework <id> "use the Input component from the design system"` | Back to `build` at round + 1; `rework-<n>.md` exists and the new build.md addresses it |
| 6d Gate failure | Temporarily break a test on the job branch via `/factory rework` asking for it (or inject `FACTORY_GATE_CMDS`) | `needs-human / gate-failed`, with the exit code and log visible in the drawer |

---

## Step 7: Phase 2, parallel jobs in worktrees

**Prompt:**

> Add parallel execution. `factory/scripts/run-job.ps1 <id>`: creates the worktree
> `..\claude-factory.wt\<id>` on branch `factory/<id>` (reusing it if it already exists), sets `FACTORY_JOB=<id>`
> and `FACTORY_HOME=<main checkout>\.factory-runs` (resolved from `git rev-parse --git-common-dir`),
> and runs `claude -p "/factory run <id>" --output-format json` in the worktree. It records the result's
> `total_cost_usd` and `duration_ms` on the job via a new `factory.ps1 set-cost` command. `merge`
> takes `.factory-runs/merge.lock` (an exclusive file create, with a timeout →
> `merge-blocked`) and merges from the main checkout, never from the worktree. `factory/scripts/run-many.ps1 -Count 3`
> creates jobs from the next 3 backlog items and starts run-job for each as a separate process.
> Remove the worktree after `merged`, and keep it on needs-human. Pester tests first: the lock
> serializes two concurrent merges, FACTORY_HOME resolution from inside a worktree finds the
> main checkout, and set-cost writes the board.

Headless runs need permissions settled in advance. Add a narrow `permissions.allow` for the
commands the factory uses (dotnet, npm, git, pwsh factory/scripts/*) in `.claude/settings.json`
rather than using `--dangerously-skip-permissions`.

**Test:** the Pester tests go red, then green. Then reset the backlog (add three new small features)
and run `run-many.ps1 -Count 3`. The dashboard should show **three cards moving at once**, with
pipeline nodes carrying several job badges. The merges should land one at a time, main should stay
clean, and every card should show its cost.

---

## Step 8 (optional): Hardening and measurement

- **Write guard:** a `PreToolUse` hook on Write|Edit that denies reviewer and approver agents any path
  outside `.factory-runs/jobs/<id>/`. Test: a fixture payload for a reviewer writing `src/x.cs` is
  denied.
- **Budget cap:** `run-job.ps1 -MaxCostUsd` uses `--max-budget-usd` if your CLI version supports
  it; otherwise it marks the job `needs-human/escalated` when the recorded cost goes over.
- **Scorecard strip on the dashboard:** jobs merged vs escalated, mean rounds, cost per merged job,
  and the gate-failure rate. These are the paper's "accepted durable outcome" measures, not counts
  of lines or PRs.
- **Later:** GitHub issue intake (`gh issue list --label factory`) feeding `new-job`.

## Validated means

The factory is validated when you have looked at the merged features yourself and judged them
acceptable, and the cost and time per merged job from steps 5–7 are worth it compared with doing
the work by hand. Record the verdict and the numbers in the vault.

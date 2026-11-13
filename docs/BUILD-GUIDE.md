# Build guide: finishing the factory

**The target:** you type an idea into the dashboard, and the spec-writer turns it into backlog cards.
You edit each spec and drag the ones you want to **Approved**. Each approved card is built, reviewed
by four agents, judged by the approver and gated, then lands in **Ready for review**. You inspect it
and drag it to **Done**, which merges it, or send it back with a rework note. The contract for all
of this is `docs/FACTORY-CONTRACT.md`. Read it before any step.

**What already exists:** the contract, the artifact templates and the empty folders are in place, and
`factory/backlog.md` holds sample ideas. Everything below is still to do.

Each step gives a prompt to paste into Claude Code in `D:\code\ai-factory`, the test that has to
be **seen red before it goes green**, and the evidence that closes the step. Do the steps in order,
committing each one on `main` once its evidence is in.
- Steps 1–3 build the deterministic parts and can be tested without spending a token.
- Steps 4–5 add the agents.
- Steps 6–7 prove it on real work.

Keep a scratch log of the wall-clock time and the cost figures for steps 6–7. The dashboard shows
cost per card from step 2 onward. Those numbers are half of the "validated" bar.

---

## Step 1: Control plane (`factory.ps1`), test-first with Pester

**Prompt:**

> Read docs/FACTORY-CONTRACT.md. Using TDD with Pester 6, build `factory/scripts/factory.ps1`: one
> script with every subcommand in the contract's "factory.ps1 commands" table, enforcing the
> "Job transitions" table exactly. Write `factory/tests/factory.Tests.ps1` FIRST, and run it and show me
> it failing before you write the script. Tests use a temp git repo fixture with a `main` branch and
> `$env:FACTORY_HOME` pointed at a temp dir, never this repo's state. Inject the gate commands
> through `$env:FACTORY_GATE_CMDS`, using `exit 0` / `exit 1`.

**The tests must cover at least the following:**
- `new-idea` allocates `I-001`; `add-spec` twice from it allocates `J-001` and `J-002` in `backlog`,
  with titles taken from the `# Spec:` heading and both ids listed on the idea; `idea-done -Failed` keeps the reason.
- `edit-spec` replaces spec.md in `backlog` and is refused in any other stage.
- An illegal transition (`backlog → done`, `approved → ready`) exits non-zero, and the board is byte-for-byte unchanged.
- `next` returns the oldest `approvedAt`, and returns nothing while any job is in build/review/approve/gate.
- `approved → build` is refused while another job is in Building; `build → review` is refused until `round-<n>/build.md` exists.
- Four `PASS` → `review → approve`. One `CHANGES` at round 1 → `build`, round 2, reviews reset to `pending`.
  `CHANGES` at round 2 → `ready` with flag `max-rounds`.
- A missing or malformed verdict line counts as no verdict, and the job doesn't move.
- `Decision: ESCALATE` → `ready` with flag `escalated`; `APPROVE` → `gate`.
- `gate` green → `ready` with no flag and `gate.sha` set. A red gate → `ready`, flag `gate-failed`, exit codes on `gate`, output in `gate.log`.
- `merge` with a dirty fixture tree → stays `ready`, flag `merge-blocked`, nothing merged.
- `merge` on a job flagged `max-rounds` (no gate recorded) runs the gate first. If the gate is red, the job
  stays `ready` with `gate-failed`; if it's green and the tree is clean, a `--no-ff` merge commit lands on `main` and the job is `done`.
- `rework` from `ready` → `approved`, round + 1, the note in `rework-<n>.md`, flag cleared.
- `discard` from `backlog` and from `ready` → `discarded`, and the `factory/<id>` branch is deleted.
- `set-cost` adds to `costUsd`/`durationMs` and appends a `cost` event.
- Every transition appends a `stage` event to `events.jsonl` with `by`.

**Evidence:** red output, then `Invoke-Pester factory/tests` green.

---

## Step 2: Runner and hooks, test-first with Pester

**Prompt:**

> Read the "Runner" section and the Agents table of docs/FACTORY-CONTRACT.md. TDD with Pester again.
> 1. `factory/scripts/runner.ps1` per the contract. The claude command is injectable through
>    `$env:FACTORY_CLAUDE_CMD`, so tests use a fake that prints a JSON result (`total_cost_usd`,
>    `duration_ms`) and drives the job with factory.ps1 calls, or exits 1.
> 2. `factory/scripts/hook-event.ps1`: reads SubagentStart/SubagentStop hook JSON from stdin and appends
>    agent-start/agent-stop through `factory.ps1 event` for `$env:FACTORY_JOB`, or for the idea in `$env:FACTORY_IDEA`.
>    It exits 0 silently when neither is set, so hooks never break a normal session. First confirm
>    the current SubagentStart/SubagentStop input fields (agent type/name) from the Claude Code hooks
>    documentation, and capture sample payloads as fixtures; don't assume the fields.
> Write the tests first and show them failing.

**The tests must cover at least the following:**
- A second runner started while `runner.lock` exists exits 0 at once and does nothing.
- Two approved jobs drain strictly in order: the second goes to `build` only after the first leaves Building.
  The lock is gone afterwards.
- The worktree is created once and reused for the second job, and the main checkout's branch and status are untouched.
- A reworked job reuses its existing `factory/<id>` branch.
- `FACTORY_HOME` resolved from inside the worktree finds the main checkout's `.factory-runs`.
- The fake claude's cost and duration land on the job, and `run-<n>.json` is saved.
- A fake claude that exits 1 while the job is in Building → `ready` with flag `escalated`.
- hook-event: each fixture payload appends the right event; with no job or idea set, it appends nothing and exits 0.

**Evidence:** red output, then `Invoke-Pester factory/tests` green.

---

## Step 3: Dashboard, server and simulator

**Prompt:**

> Build the dashboard per the "Dashboard API" and "Board columns" sections of docs/FACTORY-CONTRACT.md, test first.
> 1. `factory/scripts/simulate.ps1 [-Speed 1.0] [-Reset]`: using ONLY factory.ps1 calls, with
>    `$env:FACTORY_HOME` at `.factory-runs-sim/` and a throwaway sandbox repo at `.factory-runs-sim/repo`, it drives:
>    - an idea into three backlog cards, and a vague idea that fails
>    - `J-001` through approved → build → review (agent-start/stop for all 4 reviewers in parallel) →
>      approve → gate → ready → done
>    - `J-002` through a CHANGES round to `ready`/`max-rounds`, then rework → approved → … → `max-rounds` again
>    - `J-003` to `ready`/`merge-blocked` (dirty sandbox at merge), then cleans the sandbox
>    - `J-004` discarded from backlog, and `J-005` left in backlog
>
>    The gate and merge use injected commands; the simulator never touches this repo's git, dotnet or npm.
> 2. `factory/scripts/serve.ps1 [-Port N] [-StateDir]` (no -Port: a random free port, printed at start): an HttpListener on localhost only, with the GETs
>    and the write endpoints in the contract, each a thin wrapper over one factory.ps1 call. It starts
>    `runner.ps1` and `spec-run.ps1` as the contract says (Pester-test spec-run first), using
>    `$env:FACTORY_CLAUDE_CMD` when it is set.
> 3. A Playwright test in `factory/tests/dashboard/` (its own package.json). It starts serve.ps1 against
>    `.factory-runs-sim` with `FACTORY_CLAUDE_CMD` set to a no-op, and asserts the following:
>    - After simulate.ps1 at high speed: J-001 is in Done, J-002 and J-003 are in Ready for review
>      with their flag reasons visible, the reviewer chips show PASS/CHANGES, and J-001's drawer has
>      ≥ 10 timeline events, a working link to spec.md, and a cost badge.
>    - Submitting an idea in the idea box adds a card to Ideas.
>    - Editing a backlog card's spec in the drawer and saving it changes spec.md on disk.
>    - Dragging a backlog card to Approved moves it, and board.json shows `approved`.
>    - Dragging a backlog card straight to Done snaps back and shows the refusal message.
>    - Dragging a ready card to Done calls merge (injected green) and lands it in Done.
>    - Rework from a ready card with a note moves it to Approved.
>
>    Run it and show it failing.
> 4. Then build `factory/dashboard/index.html` (one file, no build step, no CDN dependencies) until it passes:
>    - an idea box at the top
>    - a pipeline strip that redraws the video's graph: spec-writer → builder → [security, code, ux, ui] →
>      approver → gate. A node pulses while an agent-start has no matching agent-stop, and shows a badge
>      for each job or idea id it's working on.
>    - a Kanban board with the six columns from the contract. Drag and drop only into the columns the
>      contract allows. Cards animate between columns (a FLIP transition) and show the id, title,
>      sub-stage and round in Building, reviewer chips, the flag badge, elapsed time, and cost.
>    - a job drawer with an editable spec (Backlog only, with Save), links to the artifacts shown as
>      preformatted text, the timeline, the gate exit codes and sha, and the flag reason + detail. On a
>      ready card it has Rework (with a note), Discard, and "Merge" (the same as dragging to Done).
>    - polling of board.json and events.jsonl every 1.5 s, with light and dark themes.

**Evidence:** the Playwright test red, then green. Then **watch it by eye**: run `serve.ps1` and
`simulate.ps1 -Speed 0.5`, open the URL `serve.ps1` prints, and confirm the cards visibly travel the
board and the pipeline nodes light up.

---

## Step 4: Agents

**Prompt:**

> Write the 7 agents in `.claude/agents/` per the Agents table in docs/FACTORY-CONTRACT.md. Each one gets
> frontmatter with `name`, `description`, `model` (opus/sonnet as in the table) and a minimal
> `tools` list. Reviewers and the approver get Read, Grep, Glob and Bash (for git diff and running tests)
> plus Write, and may write only their own output file. The builder gets the full edit tools.
> Each prompt states its exact input files, its exact output path, and that the output must follow
> `factory/templates/<kind>.md`, with the verdict line first.
>
> The spec-writer gets Read, Grep, Glob and Write, and writes only under `ideas/<id>/`. It splits an idea
> into the fewest independently buildable specs that cover it (1..5), each small enough for one build round,
> with every AC observable and named with the test or command that proves it. When an idea is too vague
> to spec, it writes no specs and explains why, so the idea is marked failed.
>
> Reviewer prompts include:
> "Return CHANGES only for a blocking defect you can point to at file:line, with a fix; everything else is a Note."
> (This is the counter to the over-escalation seen in the video.) Each reviewer's focus:
> - **Security:** secrets/PII in logs, authz on new endpoints, input validation and injection.
> - **Code:** correctness, tests that actually assert the ACs, and the contract regeneration rules in CLAUDE.md.
> - **UX:** behaviour against the spec's ACs from a user's point of view.
> - **UI:** docs/DESIGN.md compliance (no raw values, components from src/components/ui).
>
> The approver decides APPROVE only if every AC is evidenced and every review is PASS. Otherwise it
> returns ESCALATE with the single decision a human must make.

**Test:** this step has no executable test of its own. Its proof is step 6. Before moving on,
review the seven files yourself: this is where the factory's quality bar lives.

---

## Step 5: Orchestrator skill, hooks and permissions

**Prompt:**

> Write `.claude/skills/factory/SKILL.md`, the /factory orchestrator. It has three commands, which are
> what serve.ps1 and runner.ps1 invoke:
> - `/factory spec <ideaId>`: run the spec-writer, then `add-spec` for each spec file and `idea-done`
>   (or `idea-done -Failed` if it wrote none).
> - `/factory run <id>`: for a job already in `build` in the current worktree, run the builder, then the
>   4 reviewers as 4 parallel Agent calls in ONE message, then `read-reviews`, looping per the contract;
>   then the approver, `read-decision` and `gate`. It stops when the job reaches `ready`.
> - `/factory status`: print `factory.ps1 status`.
>
> Rules: the orchestrator stays thin. It never writes code or artifacts itself; every state change is
> a factory.ps1 call; it never merges, approves or reworks (those are human drags); and when it gives
> up, it calls `set-stage <id> ready -Reason escalated -Detail "<why>"`.
>
> Then `.claude/settings.json`:
> - SubagentStart and SubagentStop hooks calling `pwsh -NoProfile -File factory/scripts/hook-event.ps1`.
> - A narrow `permissions.allow` for the headless runs: dotnet, npm, npx, git, `pwsh factory/scripts/*`,
>   and Edit/Write. Not `--dangerously-skip-permissions`.

**Test:** a smoke run. `/factory status` on an empty board prints an empty board without error.
Then type a trivial idea into the dashboard (serve.ps1 with no `FACTORY_CLAUDE_CMD`) and confirm that
it becomes at least one backlog card with a sensible spec, and that the spec-writer pulse showed on
the pipeline strip. Discard the card afterwards.

---

## Step 6: First real job, end to end

1. Terminal A: `pwsh factory/scripts/serve.ps1`, then open the URL it prints. That's the only terminal you need.
2. Paste the "Search filter" idea from `factory/backlog.md` into the idea box.
3. Read the spec that comes back, tighten it in the drawer, and save it. Drag the card to **Approved**.
4. Watch the card move through Building and the pipeline nodes light up. When it reaches **Ready for review**, inspect
   the branch in the worktree (`aspire run` from the target's work dir, then its list page). If you're happy, drag it to **Done**.

**Pass criteria (check each one; don't trust the summary):**
- The idea produced a sensible backlog card, and your spec edit is what the builder built.
- The card ends in **Done**, and the timeline shows all 7 agents starting and stopping.
- `.factory-runs/jobs/J-001/` has spec.md, round-1/build.md, the 4 reviews, decision.md, gate.log and run-1.json.
- The gate exit codes are all 0, `git log --graph --oneline -5` shows the `--no-ff` merge of `factory/J-001`,
  and `git status` on main is clean, both during and after the run.
- The card shows cost and duration.
- `dotnet test` and `npm run build` are green on the target's main.
- `aspire run` on the target's main, then its list page: the filter actually works. This hands-on check is the one that counts.

---

### Step 6 re-run (after the first-run feedback)

The first run (I-001, a todo app) built inside the factory repo's own sample app even though the work was meant to be a separate
app. Since then: every idea names a **target repo** (an existing repo, or a path that becomes a new
one); the spec carries `## Target` and `## Gate`; the factory runs from `factory/plugin` in the
target's own `<target>.wt`; the card opens a large modal (Edit | Preview, Artifacts, Output,
Timeline); Done lists the full paths the merge added. See "Targets" in `docs/FACTORY-CONTRACT.md`.

1. Restart `serve.ps1`. Discard the legacy J-001 and J-002.
2. Submit the idea with a target you type, for example `D:\code\Scratchpad\todo-app`. The hint
   must say "New repo".
3. In Backlog, check each spec's `Repo:` is exactly your path and its `## Gate` commands make sense
   for the stack; edit in the modal and Save, then Approve spec 1 first (later specs depend on it).
4. Watch the **Output** tab: orchestrator lines, then each agent's narration and tool calls.
   (Claude Code records thinking blocks without text, so they show as a marker only.)
5. At Ready for review, run the app from `<target>.wt`; drag to Done; the modal lists the added
   files by full path, and the merge is on the target's `main`, not this repo's.

### Ideas move into a Claude session

Ideas no longer come from the dashboard. `pwsh factory/scripts/idea.ps1 <target | I-NNN>` opens an
interactive session on `/factory:idea`: the plugin's `grill` skill (ported from `ai-dlc-grill`)
interrogates you until nothing is assumed, the confirmed brief goes to `ideas/<id>/brief.md`, the
spec-writer specs from it, and you approve, change, defer or reject each spec in the session. An
approved spec goes straight to Approved and the session starts the runner. `spec-run.ps1`, the idea
form, `POST /api/ideas` and `GET /api/target` are gone. A merged bootstrap leaves its idea
`drafting`; resume it with `idea.ps1 I-NNN`. Finished cards are cleared with `factory.ps1 purge`
(the Done column's **Clear**), and `board.seq` keeps ids from being reused. The step 5 and 6
instructions above that mention the idea box and `/factory spec` describe the earlier design.

### Ideas come back to the dashboard, on the Claude API

The CLI session works, but it means a second window and a terminal UI for clicking through
questions. **New idea** now runs the same session in a dashboard panel against the Claude API, with
the key in `.env` (see "Idea sessions" in the contract). The model only proposes things. Each board
write is a button, and each button is one `factory.ps1` call (`check-target`, `new-idea`,
`add-spec`, `approve`, `idea-done`). Each turn is a detached `idea-turn.ps1` that streams into
`sessions/<S>/stream.jsonl`. The system prompt is assembled from the grill skill, the spec-writer and
the templates, so the two doors can't drift apart. The key is read into a local variable and never
set in the environment, so the runner's `claude` stays on the subscription. Tests use
`factory/tests/fixtures/anthropic-stub.ps1`, which plays scripted SSE replies (Pester
`idea-turn.Tests.ps1` and `serve.Tests.ps1`, and the Playwright session test). Roadmap spec 10 later
moved each turn onto the `claude` CLI (`claude -p`, the run mode's provider, the idea tools as an MCP
server, no API key); see "Idea sessions" in the contract. `idea.ps1` stays as the
fallback.

## Step 7: Failure paths (the video's lessons, as tests)

Run each one and confirm the end state on the dashboard and in `board.json`.

| Case | How | Expected |
| --- | --- | --- |
| 7a Dirty tree at merge | Take the "Owner on the resource" idea to Ready for review. `echo x >> README.md` on main, then drag to Done | Stays in Ready with `merge-blocked`. Then `git checkout README.md` and drag again → Done |
| 7b Security rejection | Idea: "Log every incoming request including all headers to the console" | The security reviewer returns CHANGES (auth headers / PII) → round 2 → either fixed (headers redacted) or Ready with `max-rounds` |
| 7c Human rework | On any ready card: Rework, "use the Input component from the design system" | Back to Approved at round + 1, then built again; `rework-<n>.md` exists and the new build.md addresses it |
| 7d Gate failure | Rework asking for a deliberately failing test (or inject `FACTORY_GATE_CMDS` in serve.ps1's environment) | Ready with `gate-failed`; the exit code and log are visible in the drawer, and dragging to Done is refused |
| 7e Queue | Approve two backlog cards back to back | The second waits in Approved until the first reaches Ready; never two cards in Building |
| 7f Discard | Discard a backlog card and a ready card | Both disappear, and the ready card's branch is gone |

---

## Step 8 (optional): Hardening and measurement

- **Write guard:** a `PreToolUse` hook on Write|Edit that denies the spec-writer, reviewers and
  approver any path outside their own `ideas/<id>/` or `jobs/<id>/` folder. Test: a fixture
  payload for a reviewer writing `src/x.cs` is denied.
- **Budget cap:** `runner.ps1 -MaxCostUsd` uses `--max-budget-usd` if your CLI version supports it.
  Otherwise it moves the job to `ready`/`escalated` when the recorded cost goes over.
- **Scorecard strip on the dashboard:** jobs done vs flagged, mean rounds, cost per done job,
  how often you reworked, and the gate-failure rate. These are the paper's "accepted durable
  outcome" measures, not counts of lines or PRs.
- **Later:** GitHub issue intake (`gh issue list --label factory`) feeding `new-idea`.

## Validated means

The factory is validated when you have looked at the merged features yourself and judged them
acceptable, and the cost and time per merged job from steps 6–7 are worth it compared with doing
the work by hand. Record the verdict and the numbers in the vault.

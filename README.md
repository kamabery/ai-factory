# claude-factory

A Claude Code software factory with a human at both ends: you approve the spec and accept the
result, and agents do everything in between (build → four parallel reviewers → approver →
deterministic gate). It builds GitHub target repos scaffolded from `template-projects`, and a
live dashboard is where you work it.

- **Status:** the factory is built (guide steps 1–5) and tested; it has not run a real job yet.
- **Contract:** [docs/FACTORY-CONTRACT.md](docs/FACTORY-CONTRACT.md) covers stages, files, board and event formats.
- **Rules for agents:** [CLAUDE.md](CLAUDE.md).
- **GitHub PAT setup:** [docs/GITHUB-PAT.md](docs/GITHUB-PAT.md).
- **Tests:** [docs/PESTER-SUITE.md](docs/PESTER-SUITE.md) covers what the Pester suite tests and how it runs offline.

## Getting started

Steps 1–5 of [docs/BUILD-GUIDE.md](docs/BUILD-GUIDE.md) are built: the control plane, runner,
dashboard, agents and `/factory` skill. Step 6, the first real job, is yours. To see the board move
without spending tokens, run the simulator first (see "Checking without spending tokens" below).

**Prerequisites:** .NET 10 SDK, Aspire CLI, Node 22+, PowerShell 7 with Pester 6, git, and the
`claude` CLI signed in.

### 1. Start the dashboard

```powershell
pwsh factory/scripts/serve.ps1        # prints its URL (a random free port; -Port N to fix one)
```

That's the only process you start. The dashboard starts the agent runs itself. Targets are GitHub
repos (`owner/name`): put a GitHub PAT in `GITHUB_TOKEN`, and set `GITHUB_OWNER`, in `.env` (see
`.env.example`). [docs/GITHUB-PAT.md](docs/GITHUB-PAT.md) says how to create the PAT and which
permissions it needs. Each job
builds and is tested in its own temp clone (`%TEMP%\claude-factory\jobs\J-NNN`), which is deleted
when the job reaches Done or is discarded.

### 2. Work out an idea with Claude

Put your API key in `.env` at the repo root (copy `.env.example`; it is gitignored). Then press
**New idea** in the Ideas column, give the target repo (`owner/name`; a repo that doesn't exist yet
is created, private, and scaffolded first) and a sentence, and the whole session runs in
the panel against the Claude API (`claude-opus-5-5` unless `FACTORY_IDEA_MODEL` says otherwise).
Samples are in [factory/backlog.md](factory/backlog.md).

Claude grills you in rounds of clickable questions until nothing in the idea is assumed, and reads
the brief back for your yes. It then splits the brief into 1–5 small specs, and each spec card asks
you to approve it, change it (edit it, or ask Claude), choose "not yet", or reject it. Approved
specs land in **Approved** and the runner starts. "Not yet" goes to **Backlog**, and rejected specs
never reach the board. The panel shows the session's API cost as it goes. Closing the browser loses
nothing: the session's card reopens it.

Only the idea session uses the API key. Build, review, approve and gate run on the `claude` CLI and
your subscription, as before, and the key is never put in the environment. With no key, or if you
prefer a terminal, the CLI session still works:

```powershell
pwsh factory/scripts/idea.ps1 kamabery/todo-app   # or an I-NNN to resume one
```

### 3. Backlog (optional)

Open a backlog card, read its spec (intent, acceptance criteria, scope, risks), edit it in the
drawer and save. When it says what you want built, drag it to **Approved**. Discard the cards you
don't want.

Approved is a FIFO queue. Jobs build one at a time.

### 4. Building (hands off)

The card moves into **Building** and the pipeline strip lights up as each agent runs:

1. **builder:** implements the spec on branch `factory/<id>`.
2. **security, code, UX and UI reviewers:** run in parallel. Any `CHANGES` sends the job back to
   the builder for a second round.
3. **approver:** checks every acceptance criterion against the evidence.
4. **gate:** `dotnet build`, `dotnet test` and `npm run build` must all exit 0.

### 5. Review → Done, Rework or Discard

The job lands in **Ready for review**, and its branch is pushed with a pull request (the **PR** link
in the drawer). Read the diff on GitHub, or run `aspire run` in its work dir, and read the build,
reviews and decision in the drawer. Then:

- **Drag to Done** to merge the PR (a merge commit into `main`) and delete the work dir. This is the
  only way anything merges.
- **Rework** with a note to send it back to the Approved queue for another build.
- **Discard** to drop the job, close its PR, and delete its branch and its work dir.

A red badge on the card means the factory gave up or refused, and why:

| Badge | Meaning | What to do |
| --- | --- | --- |
| `max-rounds` | Reviewers still want changes after round 2 | Read the reviews; rework, or drag to Done to overrule them |
| `escalated` | The approver or an agent needs a human decision | Read `decision.md`; rework or discard |
| `gate-failed` | Build or tests failed | Rework; Done is refused until the gate is green |
| `merge-blocked` | Nothing to merge, uncommitted changes in the work dir, or GitHub won't merge the PR (conflict, branch protection) | Read the detail; commit or revert the change, or resolve the PR, then drag to Done again |

Card cost and duration feed the "validated" bar in the build guide.

### Checking without a browser

```powershell
pwsh factory/scripts/factory.ps1 status   # the board as text
Invoke-Pester factory/tests               # control plane and runner; see docs/PESTER-SUITE.md
```

### Checking without spending tokens

```powershell
pwsh factory/scripts/simulate.ps1 -Reset -Speed 0.5              # fake jobs, against its own GitHub stub
cd factory/tests/dashboard; npm ci; npx playwright test          # the dashboard tests (serve + both stubs)
```

Factory state lives in `.factory-runs/` (gitignored). Never edit it by hand; see [CLAUDE.md](CLAUDE.md).

## Layout

```
.claude/agents/            7 factory agents (step 4)
factory/plugin/            agents, /factory run | status, /factory:idea, grill, hooks
factory/backlog.md         sample ideas for /factory:idea
factory/templates/         spec / build / review / decision formats
factory/scripts/           factory.ps1 (control plane), runner.ps1, idea.ps1, idea-turn.ps1, serve.ps1, simulate.ps1, hook-event.ps1
factory/dashboard/         index.html, the live view
factory/tests/             Pester + Playwright
.factory-runs/             runtime state (gitignored)
```

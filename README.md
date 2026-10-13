# claude-factory

A Claude Code software factory with a human at both ends: you approve the spec and accept the
result, and agents do everything in between (build → four parallel reviewers → approver →
deterministic gate). It is installed into **Shelf**, a small .NET 10 + Aspire + React app, and a
live dashboard is where you work it.

- **Status:** the factory is built (guide steps 1–5) and tested; it has not run a real job yet.
- **Contract:** [docs/FACTORY-CONTRACT.md](docs/FACTORY-CONTRACT.md) covers stages, files, board and event formats.
- **Rules for agents:** [CLAUDE.md](CLAUDE.md).

## Getting started

Steps 1–5 of [docs/BUILD-GUIDE.md](docs/BUILD-GUIDE.md) are built: the control plane, runner,
dashboard, agents and `/factory` skill. Step 6, the first real job, is yours. To see the board move
without spending tokens, run the simulator first (see "Checking without spending tokens" below).

**Prerequisites:** .NET 10 SDK, Aspire CLI, Node 22+, PowerShell 7 with Pester 6, git, and the
`claude` CLI signed in.

### 1. Start the dashboard

```powershell
pwsh factory/scripts/serve.ps1        # http://localhost:4317
```

That's the only process you start. The dashboard starts the agent runs itself. Keep the main
checkout on a clean `main`: jobs build in `..\claude-factory.wt`, never in your checkout.

### 2. Submit an idea → backlog

Type a feature into the idea box (samples in [factory/backlog.md](factory/backlog.md)). The
spec-writer splits it into 1–5 small specs, and each one appears as a card in **Backlog**. If the
idea is too vague to spec, it stays in **Ideas**, marked failed, with the reason.

### 3. Edit and approve

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

The job lands in **Ready for review**. Inspect the branch: run `aspire run` in `..\claude-factory.wt`,
and read the build, reviews and decision in the drawer. Then:

- **Drag to Done** to merge it (`--no-ff` into `main`). This is the only way anything merges.
- **Rework** with a note to send it back to the Approved queue for another build.
- **Discard** to drop the job and delete its branch.

A red badge on the card means the factory gave up or refused, and why:

| Badge | Meaning | What to do |
| --- | --- | --- |
| `max-rounds` | Reviewers still want changes after round 2 | Read the reviews; rework, or drag to Done to overrule them |
| `escalated` | The approver or an agent needs a human decision | Read `decision.md`; rework or discard |
| `gate-failed` | Build or tests failed | Rework; Done is refused until the gate is green |
| `merge-blocked` | `main` is dirty or the merge conflicts | Clean or resolve `main`, then drag to Done again |

Card cost and duration feed the "validated" bar in the build guide.

### Checking without a browser

```powershell
pwsh factory/scripts/factory.ps1 status   # the board as text
Invoke-Pester factory/tests               # control plane and runner
```

### Checking without spending tokens

```powershell
$env:FACTORY_REPO = "$PWD/.factory-runs-sim/repo"; $env:FACTORY_WORKTREE = "$PWD/.factory-runs-sim/repo.wt"
$env:FACTORY_CLAUDE_CMD = 'exit 0'
pwsh factory/scripts/serve.ps1 -StateDir .factory-runs-sim      # terminal A
pwsh factory/scripts/simulate.ps1 -Reset -Speed 0.5              # terminal B
cd factory/tests/dashboard; npm ci; npx playwright test          # the dashboard tests
```

Factory state lives in `.factory-runs/` (gitignored). Never edit it by hand; see [CLAUDE.md](CLAUDE.md).

## Layout

```
.claude/agents/            7 factory agents (step 4)
.claude/skills/factory/    /factory spec | run | status (step 5)
factory/backlog.md         sample ideas for the idea box
factory/templates/         spec / build / review / decision formats
factory/scripts/           factory.ps1 (control plane), runner.ps1, spec-run.ps1, serve.ps1, simulate.ps1, hook-event.ps1
factory/dashboard/         index.html, the live view
factory/tests/             Pester + Playwright
.factory-runs/             runtime state (gitignored)
src/ tests/ contracts/     Shelf: shelf.api, shelf.data, sqlite migrations, servicedefaults, ui/react
.aspire/apphost.cs         file-based AppHost (api + ui)
IaC/                       Terraform for Azure (from the scaffold)
```

## Shelf

Reproduce the scaffold with:

```
dotnet new aspire-base     -n Shelf -o claude-factory --dbProvider sqlite --resource Book
dotnet new aspire-ui-react -n Shelf -o claude-factory --resource Book
```

Run it with `aspire run`; there is no Docker requirement because the store is SQLite. The API serves
Swagger at `/api-docs` and returns 404 at `/`. The UI has `/books`, plus `/styleguide`, which
renders every approved component; `docs/DESIGN.md` holds the rules behind it.

Deployment is in `IaC/README.md`: Container Apps for the API, a Static Web App for the React UI,
and `.github/workflows/deploy.yml` with three OIDC secrets.

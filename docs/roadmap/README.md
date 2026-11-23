# Roadmap: specs and run order

This turns the approved replatform roadmap (2026-10-23, revised 2026-11-08) into specs a human
session can pick up one at a time. The rationale and the decisions table live in
`C:\Users\kamab\.claude\plans\grill-me-on-the-silly-yao.md`; this folder is the work.

**Where things stand (2026-11-23):** specs 00, 19, 01–14 and 20 are merged (13 in PR #22, 14 in PR #24, 20 in PR #26),
15 in PRs #25 and #27: the factory is the .NET `factory` CLI, the PowerShell control
plane and Pester are gone, and `factory init` / `doctor` set a machine up with a GitHub App (now the only GitHub
credential) and the OS secret store; nothing but spec 16's setup scripts needs PowerShell. 15's Windows acceptance run
passed in 14:16 (the Ubuntu VM run was dropped); the repos are public and `ai-factory` 0.1.0 is on NuGet. 17 (the React
dashboard) is merged (PR #30). 16 is done: the preview setup is Terraform (`factory/infra/previews`), adopted live with no change to Azure, and **deploys are back on**: a canary and a real job previewed from the .NET implementation and were torn down. The repo has no PowerShell. 18 (cloud mode on Container Apps) is done (2026-11-20), merged in PR #32 (2026-11-21): a deployed factory on Foundry with no key, proven live by a merged job with a preview. 21 (the new-repo session flow) is done, merged in PR #33 (2026-11-21): one brief confirmation and one merge take a new repo to its specs, proven in the cloud on kamabery/pantry. 23 (a scaffolded app's front door) is done, merged in PR #35 (2026-11-22): the style guide is dev-only in the template, and every new-app spec decides Home, proven by booklist on the local board and pantry on the cloud board. 22 (a Canary column that says why a canary is due, no Backlog column, and a New repo idea that scaffolds to a PRODUCT.md) is done (2026-11-23), merged in PR #36 (2026-11-23): proven live on the local board by recipebox (New repo to PRODUCT.md, the bootstrap, then its first feature waiting in Canary and building after the pass), and on the cloud board by ai-factory 0.4.3 applied and the `@cloud` check passing against it. 24 (the preview platform put to sleep, and woken before a canary, a deploy or a destroy) is done (2026-11-23), its PR
open: proven live on the local board by canary 35 waking a stopped server and the chip putting it back to sleep, and on
the cloud board by ai-factory 0.4.4 applied with its start/stop role and the `@cloud` check reading the chip. **Next:**
no spec is planned; 24's follow-up (auto-sleep) and 22's follow-ups are the candidates.
macOS is out of scope. Last green suites (24, Windows): .NET 1310 unit, 206 integration + 1 skipped; Vitest 120;
Playwright 45 (the whole suite) and `@cloud` 4; `terraform test` previews 5, cloud 10; no Pester. Reordered 2026-11-09 so previews come last (see below).

## Run order

Each spec is one branch and one PR, in this order. A spec starts only when the ones it depends on
are merged.

| # | Spec | Phase | Depends on | Size |
| --- | --- | --- | --- | --- |
| 00 | [Phase 0 close-out](00-phase0-closeout.md) | 0 | none | 1 session |
| 19 | [Test strategy and CI tiers](19-test-strategy.md) | 0 | 00 | 1 session |
| 01 | [.NET skeleton, CI and the oracle switch](01-dotnet-skeleton-and-oracle.md) | 1 | 19 | 1 session |
| 02 | [Factory.Core: contract types and rules](02-core-contract.md) | 1 | 01 | 1 session |
| 03 | [Factory.Store: file and blob backends](03-store.md) | 1 | 02 | 1–2 sessions |
| 04 | [Port: board and transition commands](04-port-board-transitions.md) | 1 | 03 | 1–2 sessions |
| 05 | [Port: idea and spec commands, GitHub client](05-port-ideas-github.md) | 1 | 04 | 1 session |
| 06 | [Port: git commands, gate and merge](06-port-git-gate-merge.md) | 1 | 05 | 1–2 sessions |
| 11 | [Port: evals, lessons and state admin](11-port-evals-lessons-state.md) | 1 | 04 | 1 session |
| 07 | [Port: the runner and run-mode providers](07-port-runner.md) | 1 | 06, 11 | 1–2 sessions |
| 08 | [Port: the dashboard server](08-port-server.md) | 1 | 07 | 1–2 sessions |
| 10 | [Port: idea turns on the run mode](10-port-idea-turns.md) | 1 | 07, 08 | 1–2 sessions |
| 09 | [Port: deploy, HCP and canary](09-port-deploy-canary.md) | 1 | 06 | 2 sessions |
| 12 | [Cutover: retire PowerShell](12-cutover.md) | 1 | 04–11 | 1 session |
| 13 | [EF Core store](13-ef-core-store.md) | 1 | 12 | 1–2 sessions |
| 14 | [`factory init` / `doctor`, GitHub App, secrets](14-init-doctor-github-app.md) | 2 | 13 | 2–3 sessions |
| 15 | [Turn-key acceptance and the public repo](15-turnkey-public.md) | 2 | 14, decision D1 | 1–2 sessions |
| 20 | [Retire the last PowerShell](20-retire-last-powershell.md) | 2 | 12 | 1–2 sessions |
| 17 | [React dashboard](17-react-dashboard.md) | 4 | 12 | 2–3 sessions |
| 16 | [`factory init previews`](16-init-previews.md) | 3 | 09, 14 | 2 sessions |
| 18 | [Cloud mode on Container Apps](18-cloud-mode.md) | 5 | 13, 16, 17 | 2–3 sessions |
| 21 | [New-repo idea sessions, one flow](21-new-repo-session-flow.md) | 5 | 17, 18 | 1–2 sessions |
| 22 | [A Canary column, the canary shown while it runs and why it is due, no Backlog column, and a New repo idea](22-canary-visibility.md) | 5 | 17, 18, 21, 23 | 2 sessions |
| 23 | [A scaffolded app's front door: style guide local, Home decided](23-scaffold-front-door.md) | 5 | 18 (+ a template-projects PR) | 1 session |
| 24 | [Put the preview platform to sleep, and wake it before a deploy](24-platform-sleep.md) | 5 | 16, 18, 22 | 1 session |

Phase 6 (hosted) is out of scope. The `TenantId` seam (03, 13) and per-job containers (18) are
what it needs from these specs.

```text
00 → 19 → 01 → 02 → 03 → 04 → 05 → 06 → 11 → 07 → 08 → 10 → 09 → 12 → 13 → 14 → 15
                                                            │    │    │         └→ 16 ─┐
                                                            │    │    └→ 17 ───────────┴→ 18
                                                            │    └→ 20
                                                            └──────────────┘
```

Arrows are dependencies (16 needs 09 and 14). The run order is the table's: 15, then 20, then 17, then
the devops block, 16 → 18.

**No PowerShell at the end.** The goal of the replatform is a factory with no PowerShell. Spec 12 retired the control
plane and Pester; 20 retires the rest (the gate's shell, the simulator, `test.ps1`, the tests' fake CLIs); 16 replaces
the setup scripts, the last of it. **Decided 2026-11-15: 15 is split around 20.** 15's packaging, contract and history review run first; then 20; then
15's acceptance runs and the repo going public, so the timings are taken once, on an install with no PowerShell.

**Both boards at the end (Kurt, 2026-11-21).** When the roadmap is complete, the factory runs either on this machine
(`factory serve`, a local board) or in Azure (`factory init cloud`, the deployed board of 18), whichever the user
prefers, and every feature works on both. Each is a whole factory with its own board; nothing makes a user pick one
for good. So every spec from 21 on is proven on a local board first and then in the cloud, and its Done when says
"in both modes". **Local first (Kurt, 2026-11-22):** from 22 on, live proofs (real ideas, jobs, canaries) run on
the local board, and the cloud board gets each release applied and the dashboard's read-only Playwright `@cloud` check
(`factory/tests/dashboard/cloud.spec.ts`) run against its URL, with no live cloud job and no Foundry spend. The end state is unchanged: every feature works on both. The last spec's close-out (23) also makes the repo's README offer both, with how to choose: today it
says only "a local dashboard is where you work it".

**Previews last.** A deployable job spends about 30 minutes in its preview deploy, and a template
change costs a `canary -Full`, so the port and the core product are proven without previews and the
devops work comes as one block at the end (16, then 18). The deploy port (09) is slow only because
of its live check, so 09 runs just before cutover (12 needs it ported) and its live check moves into
16, whose own live run already covers it. Until 09, the .NET runner (07) and server (08) forward
`canary`, `sweep` and the deploy commands to `factory.ps1` (D3). 11 still runs before 07, which
calls `lesson`. Spec numbers are names, not the order; sessions are one at a time, in table order.

## Decisions to make

| # | Decision | Needed by | Recommendation |
| --- | --- | --- | --- |
| D1 | Product name (not "Claude") and license | 15 (public repo) | **Decided: AI factory** (2026-11-14), **MIT** (2026-11-15); `kamabery/ai-factory` itself goes public after the history review |
| D2 | What the port's store is | 03 | Port the file and blob backends faithfully (Pester reads `board.json` directly), then EF Core in 13 with `board.json` as an export |
| D3 | Strangler routing | 01 | `factory.ps1` forwards a ported command to the .NET CLI, so agents, `serve.ps1` and the runner keep working while the port is part-way |
| D4 | Preview Terraform state in cloud mode: HCP or Azure Storage | 16 | Keep HCP; the templates depend on it |
| D5 | Sample target for the public repo: todoapp-two or a minimal "hello" | 15 | **Decided 2026-11-15: a minimal hello target** (`kamabery/ai-factory-hello`) |

## Rules for every spec

- **Tests follow `docs/TESTING.md`** (spec 19): unit-first, red before green, the coverage gates
  (90% Core, 70% elsewhere). A ported command's Pester cases are pushed down to unit tests on the
  service plus a few integration tests per command, and its `Describe` blocks are tagged `Ported`
  and run both ways. Before a port's PR, locally (no CI): `test.ps1`, `run-all.ps1 -Tag Ported` and
  `run-all.ps1 -Impl dotnet`; the plain `run-all.ps1` only when the spec also changes PowerShell that
  is not forwarded (`docs/TESTING.md`, "Before a PR", decided 2026-11-10).
- **`docs/FACTORY-CONTRACT.md` changes first.** A port reproduces the contract; it does not change
  it. A behaviour change found mid-port is its own commit, contract first.
- **A command counts as ported** when its Pester tests pass with `FACTORY_IMPL=dotnet`, its xUnit
  tests exist (seen red, then green), and `factory.ps1` forwards it. PowerShell code is deleted
  only at cutover (12), so the oracle can still run both ways.
- **Both modes:** a feature that changes the dashboard, the runner or the board is proven on a local board and on
  the cloud board before its spec is done (from 21 on; see "Both boards at the end"). From 22 on, the cloud proof is
  the released image plus the read-only Playwright `@cloud` check against the deployed board, not a live job.
- Cross-platform from the first line: no named mutex, no `-WindowStyle Hidden`, `Path.Combine`,
  `git`/`claude`/`az`/`terraform` resolved from PATH.
- A human session changes the factory, never `/factory`. Commit per step, with the suite counts in
  the message. One branch per spec, `feature/<NN>-<slug>`, PR to `main`, and Kurt merges.
- **Live proofs before 16 run with deploys off:** comment out `HCP_ORG` in `.env`, so no canary
  runs and no job deploys (contract, Deploy, "On or off"). First check that no preview is still up,
  since `sweep` and `destroy` run only while deploys are on. 16 turns them back on.
- At the end of each spec, update its **Status** line and this table.

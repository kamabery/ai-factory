# Roadmap: specs and run order

This turns the approved replatform roadmap (2026-10-23, revised 2026-11-08) into specs a human
session can pick up one at a time. The rationale and the decisions table live in
`C:\Users\kamab\.claude\plans\grill-me-on-the-silly-yao.md`; this folder is the work.

**Where things stand (2026-11-14):** specs 00, 19 and 01–11 are merged (09 in PR #19). Spec 12, cutover, is built
on `feature/12-cutover` and proven on Windows and Linux with deploys off: the factory is the .NET `factory` CLI, and the
PowerShell control plane and Pester are gone. Next is 13 (EF Core store). macOS is out of scope. Last green suites:
.NET 862 unit, 100 integration; Playwright 41; Pester (setup scripts and the simulator, until 16) 30. Reordered 2026-11-09 so
previews come last (see below).

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
| 17 | [React dashboard](17-react-dashboard.md) | 4 | 12 | 2–3 sessions |
| 16 | [`factory init previews`](16-init-previews.md) | 3 | 09, 14 | 2 sessions |
| 18 | [Cloud mode on Container Apps](18-cloud-mode.md) | 5 | 13, 16, 17 | 2–3 sessions |

Phase 6 (hosted) is out of scope. The `TenantId` seam (03, 13) and per-job containers (18) are
what it needs from these specs.

```text
00 → 19 → 01 → 02 → 03 → 04 → 05 → 06 → 11 → 07 → 08 → 10 → 09 → 12 → 13 → 14 → 15
                                                            │    │         └→ 16 ─┐
                                                            │    └→ 17 ───────────┴→ 18
                                                            └──────────────┘
```

Arrows are dependencies (16 needs 09 and 14). The run order is the table's: 15, then 17, then the
devops block, 16 → 18.

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
| D1 | Product name (not "Claude") and license | 15 (public repo) | MIT; pick the name before 14 so `factory init` prints it |
| D2 | What the port's store is | 03 | Port the file and blob backends faithfully (Pester reads `board.json` directly), then EF Core in 13 with `board.json` as an export |
| D3 | Strangler routing | 01 | `factory.ps1` forwards a ported command to the .NET CLI, so agents, `serve.ps1` and the runner keep working while the port is part-way |
| D4 | Preview Terraform state in cloud mode: HCP or Azure Storage | 16 | Keep HCP; the templates depend on it |
| D5 | Sample target for the public repo: todoapp-two or a minimal "hello" | 15 | A minimal hello target; the ~15-minute bar needs a small one |

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
- Cross-platform from the first line: no named mutex, no `-WindowStyle Hidden`, `Path.Combine`,
  `git`/`claude`/`az`/`terraform` resolved from PATH.
- A human session changes the factory, never `/factory`. Commit per step, with the suite counts in
  the message. One branch per spec, `feature/<NN>-<slug>`, PR to `main`, and Kurt merges.
- **Live proofs before 16 run with deploys off:** comment out `HCP_ORG` in `.env`, so no canary
  runs and no job deploys (contract, Deploy, "On or off"). First check that no preview is still up,
  since `sweep` and `destroy` run only while deploys are on. 16 turns them back on.
- At the end of each spec, update its **Status** line and this table.

# claude-factory

Two things live in this repo:

1. **Shelf**, the target application. It is a .NET 10 + Aspire API on SQLite with a React front end,
   scaffolded from `D:\code\template-projects` (`aspire-base` + `aspire-ui-react`, resource `Book`).
2. **The factory**, a Claude Code software factory installed into Shelf, as in the ZazenCodes
   "Build Your Own AI Software Factory with Claude Code" video. A `/factory` skill orchestrates
   spec, build, four parallel reviewers and an approver; ideas are grilled and specced in a
   dashboard idea session on the Claude API (key in `.env`), or in the CLI fallback, an interactive
   `/factory:idea` session. Each job's branch is deployed to an ephemeral Azure preview
   through HCP Terraform before the approver (`docs/DEPLOY.md`), and jobs that go bad leave lessons
   the dashboard proposes as template fixes. A deterministic PowerShell control plane
   owns state, the gate and the merge. A local dashboard is where the human works the factory:
   backlog specs edited and approved by drag and drop, and finished work reviewed, merged and cleared.

The design rationale is in `D:\code\main-vault\ai-engineering\software-factories-agentic-os-and-harnesses.md`
and `...\build-your-own-ai-software-factory-with-claude-code.md`. How to build the rest of it is in
`docs/BUILD-GUIDE.md`.

## Factory rules

- **`docs/FACTORY-CONTRACT.md` is the source of truth** for stages, transitions, file layout,
  `board.json`, `events.jsonl` and verdict headers. Change it first, then the code.
- **Every state change goes through `factory/scripts/factory.ps1`.** Never edit
  `.factory-runs/board.json` or `events.jsonl` by hand or with a file tool, not even to "fix" one.
  If the script refuses a transition, that is the factory working; escalate rather than route around it.
- **`.factory-runs/` is gitignored on purpose.** Factory state must never dirty the release checkout.
  In the source video, a modified `backlog.md` blocked an approved merge. Don't move state into
  tracked files.
- **Approval is not merge.** The approver's `APPROVE` only moves a job to `gate`, and a green gate
  only moves it to Ready for review. Only a human accepts: a drag to Done runs `factory.ps1 merge`,
  which still requires a green gate and a clean tree, or the human merges the PR on GitHub, which
  `sync-prs` records as Done (an ungated head is noted, not refused).
- **The dashboard's write endpoints are thin `factory.ps1` wrappers.** `serve.ps1` holds no
  transition logic of its own.
- **Targets are GitHub repos (`owner/name`), and every job builds in its own temp clone**,
  `<work>\jobs\<id>` (`$env:FACTORY_WORK`, else `%TEMP%\claude-factory`), one job at a time. The
  clone is deleted when the job reaches Done (its PR merged) or is discarded. Only `factory.ps1`
  holds the GitHub token (`.env`) and pushes; agents never push.
- **No agent weakens the thing that judges it.** Factory jobs never edit `factory/`, `.claude/`,
  `docs/FACTORY-CONTRACT.md` or test assertions in order to pass. Changes to the factory itself are
  made by a human session, not by `/factory`.
- **Previews are never production.** The deploy stage applies only `j<NNN>` previews; dev and
  staging are ready to deploy by hand, and nothing in the factory deploys prod.
- **Tests are seen red before green.** The factory scripts are tested with Pester
  (`Invoke-Pester factory/tests`) and the dashboard with a Playwright check against `simulate.ps1`.

## Shelf rules (from the scaffold)

- The `GetMigrations().Any()` branch in `InitializeDatabaseAsync` is load-bearing; it is what lets
  `dotnet test` run without a database. Do not make `MigrateAsync` unconditional.
- `BookSeeder` checks staleness, not emptiness.
- **`contracts/api.openapi.json` is the contract, and it is generated.** Change the endpoint, then
  `UPDATE_CONTRACT=1 dotnet test tests/shelf.api.tests --filter "Category=ContractTest"`, then
  `npm run api:types` in `src/ui/react`. Never declare a DTO by hand. See `docs/ADDING-A-RESOURCE.md`.
- Every action needs `[ProducesResponseType<T>]`; `ContractTests` fails without it.
- Neither codegen runs during a build. Both outputs are committed.
- Never edit `src/ui/react/src/routeTree.gen.ts`. If `tsc` fails on a new route, run `npx vite build` once.
- **The design system is `docs/DESIGN.md`.** Compose from `src/ui/react/src/components/ui/`,
  never use raw values, and add a missing variant to `/styleguide` first.
- `npm run lint` is oxlint, and `noUnusedLocals` is on.
- Don't add `UseHttpsRedirection` to the API.
- The data store is SQLite, baked in at scaffold time. Changing it is a code change (and
  `IaC/database.tf` changes with it).
- The API returns 404 at `/`; that is intended.

## Commands

```powershell
dotnet build shelf.slnx
dotnet test tests/shelf.api.tests
cd src/ui/react; npm install; npm run build
aspire run                                   # no Docker needed (SQLite)

pwsh factory/scripts/serve.ps1               # dashboard on a random localhost port it prints; -Port N to fix one
pwsh factory/scripts/idea.ps1 <owner/name|I-NNN> # CLI fallback for the dashboard's New idea: grill -> brief -> specs -> you approve
pwsh factory/scripts/runner.ps1              # drains the Approved queue; serve.ps1 starts it for you (after step 2)
Invoke-Pester factory/tests                  # control-plane, runner, idea-turn and serve tests (after steps 1-2)
$env:FACTORY_TEST_STATE='azure'; Invoke-Pester factory/tests/factory.Tests.ps1   # the same on the azure state backend (blob stub)
pwsh factory/scripts/factory.ps1 state-migrate | allow-ip | prune   # FACTORY_STATE=azure: state in Blob storage (docs/STATE.md)
pwsh factory/scripts/setup-azure.ps1 -SubscriptionId <id> -HcpOrg <org> [-CreateExternalTenant <sub> -ExternalResourceGroup <rg> | -ExternalTenantId <id>]  # one-time preview identities (docs/DEPLOY.md)
```

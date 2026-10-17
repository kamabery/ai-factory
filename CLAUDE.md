# claude-factory

Two things live in this repo:

1. **Shelf**, the target application. It is a .NET 10 + Aspire API on SQLite with a React front end,
   scaffolded from `D:\code\template-projects` (`aspire-base` + `aspire-ui-react`, resource `Book`).
2. **The factory**, a Claude Code software factory installed into Shelf, as in the ZazenCodes
   "Build Your Own AI Software Factory with Claude Code" video. A `/factory` skill orchestrates
   spec, build, four parallel reviewers and an approver; ideas are grilled and specced in an
   interactive `/factory:idea` session. A deterministic PowerShell control plane
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
  only moves it to Ready for review. Only a human drag to Done runs `factory.ps1 merge`, which
  still requires a green gate and a clean tree.
- **The dashboard's write endpoints are thin `factory.ps1` wrappers.** `serve.ps1` holds no
  transition logic of its own.
- **Jobs build in one reusable worktree, `..\claude-factory.wt`**, one at a time. The main checkout
  stays on a clean `main`.
- **No agent weakens the thing that judges it.** Factory jobs never edit `factory/`, `.claude/`,
  `docs/FACTORY-CONTRACT.md` or test assertions in order to pass. Changes to the factory itself are
  made by a human session, not by `/factory`.
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

pwsh factory/scripts/serve.ps1               # dashboard on http://localhost:4317 (after step 3)
pwsh factory/scripts/idea.ps1 <target|I-NNN> # Claude session: grill -> brief -> specs -> you approve -> Approved
pwsh factory/scripts/runner.ps1              # drains the Approved queue; serve.ps1 starts it for you (after step 2)
Invoke-Pester factory/tests                  # control-plane and runner tests (after steps 1-2)
```

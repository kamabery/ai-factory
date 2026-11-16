# Testing

How the factory is tested, and the rules every roadmap spec follows (spec 19). The shape is the
testing pyramid (Mike Cohn; Ham Vocke's "The Practical Test Pyramid" on martinfowler.com): many
small, fast unit tests, fewer integration tests, and a thin layer of end-to-end tests. Tiers are
defined by **what a test may touch**, after Google's small, medium and large test sizes, not by
how much code a test covers.

## Where we are, and where we are going

Until spec 12 the suite was an inverted pyramid: every Pester test ran `factory.ps1` in a real git
repo against HTTP stubs, so all 658 Pester runs were integration tests, about 18 minutes even in
parallel, and Pester was the **oracle** the .NET port was held to. Spec 12 retired the PowerShell and
pushed each of its 387 Pester cases down to a unit or integration test
([the mapping](roadmap/12-pester-mapping.md)). Spec 20 retired the rest: the simulator, the suite's runner and the
tests' fake CLIs are .NET (`Factory.Simulator`, `Factory.TestGates`, Factory.TestHost's fakes), and a gate line runs
with no shell. Spec 16 replaced the last PowerShell, the preview setup scripts, with Terraform tested by `terraform test`
([its mapping](roadmap/16-pester-mapping.md)); the repo has no Pester.
The .NET code is built unit-first from its first line (spec 01).

**Target shape:** about 80% unit, 15% integration and 5% end to end, by test count. `Factory.TestGates` reports the
counts per tier; coverage is the gate, not the ratio.

## Tiers

| Tier | May touch | Where | Runs |
| --- | --- | --- | --- |
| **Unit** (small) | memory only: no file system, process, network, real clock or environment variables | `Factory.UnitTests` (xUnit v3) | locally, before every PR that touches `src/factory` |
| **Unit, the SPA** (small) | jsdom only: components and hooks with `fetch` stubbed | `src/factory/Factory.Dashboard` (Vitest, Testing Library) | locally, before every PR that touches the dashboard |
| **Integration** (medium) | temp directories, git, child processes, localhost stubs (GitHub, HCP, blob, Anthropic: `Factory.TestHost`, hosted in process) | `Factory.IntegrationTests` (xUnit v3) | locally, before every PR that touches `src/factory` |
| **End to end** (large) | the dashboard in a browser against `Factory.Simulator` | `factory/tests/dashboard` (Playwright) | locally: the `@smoke` set before a PR that touches the dashboard or `Factory.Server`; the whole suite on demand |
| **Terraform** (the factory's own IaC) | nothing: every provider mocked | `factory/infra/*/tests/*.tftest.hcl` (`terraform test`) | locally, before a PR that touches that folder |

A test that needs a temp directory is an integration test, even if it is fast. Put it in the
integration project.

**Fakes.** A program the factory runs is replaced, in a test, through its override (`FACTORY_GH_CMD`, `FACTORY_AZ_CMD`,
`FACTORY_SIGNIN_CMD`, `FACTORY_CANARY_SCAFFOLD_CMD`, `FACTORY_BROWSER_CMD`, `FACTORY_IDEA_CLAUDE_CMD`,
`FACTORY_CLAUDE_CMD`): a JSON array naming a program and its leading arguments (contract, "Implementations"). The
fakes are Factory.TestHost's modes, `dotnet Factory.TestHost.dll <mode> [options --]`: `tool` (gh and az, logging each
call), `signin` (the sign-in smoke), `claude` (an idea turn through the MCP server and the Anthropic stub, or a runner
pass), `env` (what a child saw) and `launch` (a parent that starts the CLI detached). A fake's options go before the
`--`, so a test configures it without setting this process's environment (`Fakes.Command` in the integration tests).
Gate lines in tests are programs too: `git --version` passes, `git no-such-command` fails.

## Unit-testable design (.NET)

- **Constructor injection on the built-in container** (`Microsoft.Extensions.DependencyInjection`),
  with one composition root per host (`Factory.Cli`, `Factory.Server`, `Factory.Runner`). No service
  locator and no `new` of a collaborator inside a service.
- **Every I/O behind an interface.** Time is `TimeProvider`, the built-in one. The rest are ours:
  `IFileStore` (board, events, blobs), `IProcessRunner` (`git`, `claude`, `gh`, `az`,
  `terraform`), `IGitClient`, `IGitHubClient`, `IHcpClient`. HTTP clients come from
  `IHttpClientFactory`.
- **Settings through `IOptions<T>`.** Nothing outside a composition root reads `Environment`, `.env`,
  `DateTime.Now`, `Console` or a static file API.
- **Rules as pure functions.** `Factory.Core` (spec 02) holds the contract as types and functions
  with no I/O. Services orchestrate; Core decides.
- **One type per file**, named after the type, tests included. Each project's interfaces, and the
  records their members take or return, live in its `Abstractions` folder and namespace
  (`Factory.Core.Abstractions`, `Factory.Store.Abstractions`, `Factory.Cli.Abstractions`).

## Unit test conventions

- **xUnit v3, NSubstitute, and AutoFixture as an auto-mocking container**
  ([ploeh, 2010](https://blog.ploeh.dk/2010/08/19/AutoFixtureasanauto-mockingcontainer/)), through
  `AutoFixture.AutoNSubstitute` and `AutoFixture.Xunit3`. One shared attribute:

  ```csharp
  public sealed class AutoNSubDataAttribute() : AutoDataAttribute(() =>
      new Fixture().Customize(new AutoNSubstituteCustomization { ConfigureMembers = true }));
  ```

  `[Theory, AutoNSubData]` builds the system under test with substitutes for every dependency.
  `[Frozen]` pins the substitute the test sets up or asserts on. `[InlineAutoNSubData(...)]` is for
  table-driven cases.
- **One composition-root test per host.** It builds the real container with
  `ValidateOnBuild = true` and `ValidateScopes = true` and resolves every registered service.
  A missing registration fails here, not at run time.
- **Red before green**, as everywhere in this repo: the test is written and seen failing, then the
  code.

## Porting a command: push down

When a command was ported (specs 04–11), its Pester cases were **pushed down**, not copied:
- **Unit tests on the service** prove each case's behaviour: transitions, refusals with their exact
  text, events.
- **A few integration tests per command** prove the real adapters and the CLI's exact stdout,
  stderr and exit code.
- **The command's Pester `Describe` blocks were tagged `Ported`** and ran under both implementations
  (`FACTORY_IMPL=ps` and `dotnet`). That is how a command counted as ported.

[Spec 12's mapping file](roadmap/12-pester-mapping.md) names, for every Pester case, the unit or
integration test that covers it. [Spec 16's](roadmap/16-pester-mapping.md) does the same for the setup scripts' Pester.

## Gates

| Gate | Where | Bar |
| --- | --- | --- |
| Coverage (Microsoft.Testing.Extensions.CodeCoverage, line, unit and integration merged; `Factory.TestGates`) | before every PR, from spec 01 | **90%** on `Factory.Core`, **70%** on every other project with logic. `Factory.AppHost`, `Factory.ServiceDefaults` and the test tooling (`Factory.TestHost`, the stubs and fakes; `Factory.Simulator`; `Factory.TestGates`, whose rules have unit tests of their own) are excluded, as is generated code: source-generator output (`*.g.cs`, such as `[GeneratedRegex]`'s) and `dotnet ef` migrations (`Migrations/`) |
| SPA coverage (Vitest, v8, lines) | before a PR that touches the dashboard, from spec 17 | **70%** on `src/factory/Factory.Dashboard/src`; `npm test` fails under it. `Factory.TestGates` does not run it, so the .NET suites need no Node |
| Mutation score (Stryker.NET) | on demand, `Factory.Core` | not a gate; each port spec records the score in its Status line |
| Tier counts | before every PR | printed by `Factory.TestGates`, not a gate |

## Before a PR (no CI)

The factory's tests run **locally, not in CI**: GitHub Actions minutes cost too much for a solo
project (2026-11-09). The repo has no test workflows. Before opening a PR, run what the change
touches and put the counts in the PR description:

| The PR touches | Run |
| --- | --- |
| `src/factory/**` | `dotnet run --project src/factory/Factory.TestGates` (unit + integration, tier counts, coverage gates) |
| `factory/plugin/**` or `factory/templates/**` | `dotnet run --project src/factory/Factory.TestGates` (`PluginTests` reads them) |
| `factory/infra/<name>/**` | `terraform init -backend=false; terraform test` in that folder (offline, mocked providers); a live `terraform plan` before anyone applies |
| the dashboard (`src/factory/Factory.Dashboard`) | `npm test` and `npm run build` there (the build in `factory/dashboard/` is committed with its source), then the Playwright `@smoke` set; the whole suite for dashboard work |
| `Factory.Server` | the Playwright `@smoke` set |
| only `docs/**` or root `*.md` | nothing |
| a spec whose "Done when" names Linux | in WSL: `factory/tests/linux/setup.sh` once (a user-local .NET 10 SDK and git, no sudo), then `factory/tests/linux/run.sh test` (or `unit`, `integration`), which run on a copy of the working tree in `~/factory-test` |

## Commands

```powershell
cd src/factory/Factory.Dashboard; npm test                      # the SPA's Vitest suite and its 70% gate
cd src/factory/Factory.Dashboard; npm run build                 # rebuilds factory/dashboard/ (committed)
cd factory/tests/dashboard; npx playwright test --grep @smoke   # the smoke set
cd factory/tests/dashboard; npx playwright test                 # the whole e2e suite
dotnet run --project src/factory/Factory.TestGates               # .NET unit + integration, tier counts, coverage gates
cd factory/infra/previews; terraform test                        # the preview setup's Terraform, offline
bash factory/tests/linux/run.sh test                             # in WSL: Factory.TestGates on Linux, no pwsh
```

A Playwright test joins the smoke set with `{ tag: '@smoke' }`. Keep the set to the critical paths
(the board renders, the modal, approve, merge, rework), about five tests and about a minute.

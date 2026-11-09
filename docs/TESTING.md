# Testing

How the factory is tested, and the rules every roadmap spec follows (spec 19). The shape is the
testing pyramid (Mike Cohn; Ham Vocke's "The Practical Test Pyramid" on martinfowler.com): many
small, fast unit tests, fewer integration tests, and a thin layer of end-to-end tests. Tiers are
defined by **what a test may touch**, after Google's small, medium and large test sizes, not by
how much code a test covers.

## Where we are, and where we are going

Today the suite is an inverted pyramid. Every Pester test runs `factory.ps1` in a real git repo
against HTTP stubs, so all 658 Pester runs (429 local, 229 on the azure backend) are integration
tests, and there are no unit tests. That is why the full Pester suite needs four Windows VMs and
about 16 minutes.

The PowerShell is not reworked for unit tests: it is being retired (roadmap spec 12), and until then
Pester is the **oracle** the .NET port is held to. The .NET code is built unit-first from its first
line (spec 01).

**Target shape:** about 80% unit, 15% integration and 5% end to end, by test count. CI reports the
counts per tier; coverage is the gate, not the ratio.

## Tiers

| Tier | May touch | Where | Runs |
| --- | --- | --- | --- |
| **Unit** (small) | memory only: no file system, process, network, real clock or environment variables | `Factory.UnitTests` (xUnit v3) | every PR, on Windows, Linux and macOS |
| **Integration** (medium) | temp directories, git, child processes, localhost stubs (GitHub, HCP, blob, Anthropic) | `Factory.IntegrationTests` (xUnit v3) | every PR on Linux; all three OSes on demand and before cutover |
| **End to end** (large) | the dashboard in a browser against `simulate.ps1` | `factory/tests/dashboard` (Playwright) | the `@smoke` set on every PR; the whole suite on demand |
| **Pester** (legacy oracle, integration) | as integration | `factory/tests/*.Tests.ps1` | PRs that touch the scripts; on demand; retired at 12 |

A test that needs a temp directory is an integration test, even if it is fast. Put it in the
integration project.

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

When a command is ported (specs 04–11), its Pester cases are **pushed down**, not copied:
- **Unit tests on the service** prove each case's behaviour: transitions, refusals with their exact
  text, events.
- **A few integration tests per command** prove the real adapters and the CLI's exact stdout,
  stderr and exit code.
- **The command's Pester `Describe` blocks are tagged `Ported`** and run under both implementations
  (`FACTORY_IMPL=ps` and `dotnet`). That is how a command counts as ported.

Spec 12's mapping file names, for every Pester case, the unit or integration test that covers it.

## Gates

| Gate | Where | Bar |
| --- | --- | --- |
| Coverage (coverlet, line) | every PR, from spec 01 | **90%** on `Factory.Core`, **70%** on every other project with logic. `Factory.AppHost` and `Factory.ServiceDefaults` are excluded |
| Mutation score (Stryker.NET) | on demand, `Factory.Core` | not a gate; each port spec records the score in its Status line |
| Tier counts | every PR | reported in the run summary, not a gate |

## CI

| Workflow | When | Runs |
| --- | --- | --- |
| `suites.yml` | every PR and push to `main`, except docs-only (`docs/**`, root `*.md`) | the Playwright `@smoke` set (5 tests, one VM); from 01, also the .NET unit and integration jobs |
| `pester.yml` | PRs and pushes that touch `factory/scripts/**`, `factory/plugin/**`, `factory/tests/*.ps1`, `factory/tests/fixtures/**` | the full Pester suite, 4 shards. From 01, port PRs run the `Ported` blocks both ways instead |
| `full.yml` | **Run workflow** (`gh workflow run full.yml -f suites=all\|playwright\|pester`), or a PR labelled `e2e` (Playwright) or `full` (Pester), rerun on each push | the whole Playwright suite and/or the whole Pester suite |

`_pester.yml` and `_playwright.yml` are the reusable jobs these call.

## Commands

```powershell
cd factory/tests/dashboard; npx playwright test --grep @smoke   # the smoke set (what every PR runs)
cd factory/tests/dashboard; npx playwright test                 # the whole e2e suite
pwsh factory/tests/run-all.ps1                                  # the whole Pester suite, in parallel
gh workflow run full.yml -f suites=all                           # both full suites in CI
```

A Playwright test joins the smoke set with `{ tag: '@smoke' }`. Keep the set to the critical paths
(the board renders, the modal, approve, merge, rework), about five tests and about a minute.

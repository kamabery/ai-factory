# 19: Test strategy and CI tiers

**Status:** in progress (2026-11-09). **Branch:** `feature/19-test-strategy`. **Depends on:** 00.

## Why

The suite is an inverted pyramid: all 658 Pester runs are integration tests (`factory.ps1` in a real
git repo against HTTP stubs), there are no unit tests, and CI ran all of it, plus all 41 Playwright
tests, on every PR across five Windows VMs. Runs before the port starts, so the .NET code is built
unit-first from its first line. The rules are in `docs/TESTING.md`.

## Scope

- **`docs/TESTING.md`:**
  - tiers by what a test may touch (unit = memory only);
  - unit-testable design: constructor injection on `Microsoft.Extensions.DependencyInjection`,
    I/O behind interfaces, `TimeProvider`;
  - xUnit v3 with NSubstitute and AutoFixture as an auto-mocking container (`[AutoNSubData]`);
  - "push down" for ported commands;
  - coverage gates: 90% Core, 70% elsewhere;
  - Stryker on demand;
  - an 80/15/5 target shape, reported in CI but not gated.
- **CI:**
  - `suites.yml` runs only the Playwright `@smoke` set (5 tests) on every non-docs PR.
  - `pester.yml` runs the full Pester suite only when the scripts, plugin or Pester tests change.
  - `full.yml` runs either full suite on demand, or for a PR labelled `e2e` / `full`.
  - `_pester.yml` and `_playwright.yml` are the shared jobs.
- **Smoke tags** on five Playwright tests: the board renders, the modal, approve, merge, rework.
- **Roadmap:**
  - 01 depends on 19 and builds the .NET side (two test projects, packages, coverage gate, OS
    matrix, `-Tag Ported`).
  - 12 maps each Pester case to its covering test.
  - 17 is unit-first (Vitest).
  - The README rules point here.

## Done when

- `npx playwright test --grep @smoke --list` lists 5 tests (0 before), and they pass on their own.
- actionlint is clean.
- This spec's PR runs the smoke set. Pester triggers on it only because the PR adds `pester.yml`,
  which is in its own filter; that run was cancelled, since no PowerShell changed.
- The next docs-only PR runs nothing.
- `full.yml` from the button runs all 41 Playwright tests and the 4 Pester shards.

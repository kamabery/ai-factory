# 15: Turn-key acceptance and the public repo

**Status:** done (2026-11-15): part 1 merged in PR #25, part 2 in PR #27, the repos are public, and `ai-factory` 0.1.0 is on
NuGet (`dotnet tool install -g ai-factory`). Part 1: the contract's "Installs" (a checkout or the dotnet tool), tool installs keeping `.env`, state and the
template workspace in `~/.ai-factory`, doctor's `templates`, init cloning `FACTORY_TEMPLATES_REPO`, previews refused in a
tool install, `dotnet pack` of `ai-factory` 0.1.0, the history review. **Part 2:** the GitHub PAT fallback removed (the
App is the only GitHub credential; tests, the simulator and Playwright sign in as Factory.TestHost's `SimApp`); the
README for a stranger and the MIT `LICENSE`; found by scripting the acceptance runs and fixed, contract first: doctor's
`tools` needs a git identity, and init clones the templates as the App (template-projects was private). **Acceptance:**
the Windows Sandbox run passed, **14:16** from a fresh Sandbox (installs 1:30, the Claude sign-in, `factory init` 1:32, the
idea session 6:00, build to Ready 2:54) to J-001 in Ready for review, round 1, PR kamabery/ai-factory-hello#4; the
rehearsal's J-001 (PR #3) also passed round 1. **The Ubuntu 24.04 Hyper-V run was dropped by Kurt** (2026-11-15); Linux is
proven by the WSL suite and spec 20's WSL live job. Kit and results: `D:\code\Scratchpad\factory-15b`. template-projects
PR #7 scrubs `live.ps1`'s platform ids; ai-factory-hello PR #2 adds its LICENSE; `kamabery/ai-factory` is marked a
template. .NET 1054 unit, 196 integration + 1 skipped, gates green (Windows); 1054 / 193 + 4 skipped in WSL; Playwright
41. **Public (Kurt's yes, 2026-11-15):** gitleaks over every commit found nothing (ai-factory 210, template-projects 46,
ai-factory-hello 5), then ai-factory, template-projects and ai-factory-hello were made public; ai-factory and the hello
target are MIT. **Branch:** `feature/15-public-part2`. **Depends on:** 14, decisions D1 and D5.

**Decided 2026-11-15:** MIT; `kamabery/ai-factory` itself goes public (after the history review); D5 is a minimal hello
target, `kamabery/ai-factory-hello`; acceptance on **Windows and Ubuntu 24.04 only** (macOS out of scope), on Windows
Sandbox and a Hyper-V Ubuntu 24.04 VM. **Split around 20:** part 1 (packaging, the contract, the history review) runs
now; spec 20 runs next; part 2 (the acceptance runs and going public) runs after 20. Also decided: `template-projects`
goes public too (`factory init` clones it for a tool install); the GHCR image moves to 18 (no OS secret store or
subscription sign-in in a container; 18 builds the Container Apps image anyway); the history is scrubbed forward, not
rewritten.

**History review (2026-11-15, part 1):** gitleaks over every ref of both repos (ai-factory 175 commits,
template-projects 45): no real secrets; `.env`, `.factory-runs/` and tfstate were never committed; the two preview DB
passwords of PR #1 were only in a session's output. False positives (the fake PEM in `SecretStoreTests`) are in
`.gitleaksignore`. Scrubbed forward: the tenant and subscription ids in `docs/STATE.md`; still to do in
template-projects' own PR: the External tenant id in `tooling/verify/live.ps1`. ai-factory's 33 Actions runs, their
artifacts and caches are deleted. Author emails stay in commit metadata.

## Scope

- **Packaging:** the `dotnet tool` on NuGet (`ai-factory`, command `factory`) and "Use this template" for
  forking the plugin and agents (`kamabery/ai-factory` is marked a template). The GHCR image moved to 18.
- **Acceptance run (part 2):** a scripted fresh-VM run per OS (Windows, Ubuntu 24.04) that times install
  to first reviewed PR, against the minimal sample target (D5). The bar is 15 minutes or less.
- **The public repo:** the factory moves to its own public repo under the D1 name and license. The
  history is reviewed for secrets first (`.env`, tokens printed in logs, the two preview DB passwords
  noted in PR #1). `.factory-runs/` stays out.
- A README for a stranger: what it is, the core tier (GitHub plus an LLM), previews as an add-on, marked as still being proven until 16.

## Done when

Both acceptance runs pass the bar, and the repo is public.

# 15: Turn-key acceptance and the public repo

**Status:** part 1 done on the branch (2026-11-15), part 2 waits for spec 20. Part 1: the contract's "Installs" (a
checkout or the dotnet tool), tool installs keeping `.env`, state and the template workspace in `~/.ai-factory`, doctor's
`templates`, init cloning `FACTORY_TEMPLATES_REPO`, previews refused in a tool install, and `dotnet pack` of `ai-factory`
0.1.0 (CLI, runner, server and the factory root's files), proven by a sandboxed `dotnet tool install -g`. .NET 972 unit,
190 integration + 1 skipped, gates green (Windows). Nothing published. **Part 2 (after 20):** the hello target, the README
for a stranger, the acceptance runs (Windows Sandbox, Hyper-V Ubuntu 24.04), the PAT fallback's removal, the
template-projects scrub (`tooling/verify/live.ps1`'s platform ids) and history review there, marking the repo a
template, then (with Kurt's yes) publishing to NuGet and making both repos public. **Branch:** `feature/15-public`. **Depends on:** 14, decisions D1 and D5.

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

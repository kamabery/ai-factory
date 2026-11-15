# 15: Turn-key acceptance and the public repo

**Status:** in progress (2026-11-15), part 1 of 2. **Branch:** `feature/15-public`. **Depends on:** 14, decisions D1 and D5.

**Decided 2026-11-15:** MIT; `kamabery/ai-factory` itself goes public (after the history review); D5 is a minimal hello
target, `kamabery/ai-factory-hello`; acceptance on **Windows and Ubuntu 24.04 only** (macOS out of scope), on Windows
Sandbox and a Hyper-V Ubuntu 24.04 VM. **Split around 20:** part 1 (packaging, the contract, the history review) runs
now; spec 20 runs next; part 2 (the acceptance runs and going public) runs after 20.

## Scope

- **Packaging:** the `dotnet tool` on NuGet, a container image on GHCR, and "Use this template" for
  forking the plugin and agents.
- **Acceptance run (part 2):** a scripted fresh-VM run per OS (Windows, Ubuntu 24.04) that times install
  to first reviewed PR, against the minimal sample target (D5). The bar is 15 minutes or less.
- **The public repo:** the factory moves to its own public repo under the D1 name and license. The
  history is reviewed for secrets first (`.env`, tokens printed in logs, the two preview DB passwords
  noted in PR #1). `.factory-runs/` stays out.
- A README for a stranger: what it is, the core tier (GitHub plus an LLM), previews as an add-on, marked as still being proven until 16.

## Done when

Both acceptance runs pass the bar, and the repo is public.

# 15: Turn-key acceptance and the public repo

**Status:** not started. **Branch:** `feature/15-public`. **Depends on:** 14, decisions D1 and D5.

## Scope

- **Packaging:** the `dotnet tool` on NuGet, a container image on GHCR, and "Use this template" for
  forking the plugin and agents.
- **Acceptance run:** a scripted fresh-VM run per OS (Windows, Ubuntu, macOS) that times install
  to first reviewed PR, against the minimal sample target (D5). The bar is 15 minutes or less.
- **The public repo:** the factory moves to its own public repo under the D1 name and license. The
  history is reviewed for secrets first (`.env`, tokens printed in logs, the two preview DB passwords
  noted in PR #1). `.factory-runs/` stays out.
- A README for a stranger: what it is, the core tier (GitHub plus an LLM), previews as an add-on, marked as still being proven until 16.

## Done when

All three acceptance runs pass the bar, and the repo is public.

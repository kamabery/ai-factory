# 10: Port idea turns onto the run mode

**Status:** not started. **Branch:** `feature/10-port-idea-turns`. **Depends on:** 07, 08.

## Goal

Idea sessions (grill, brief, specs) run on `ILlmProvider` like jobs do, and stop using
`ANTHROPIC_API_KEY` locally.

## Scope

- **Local mode:** headless `claude -p --resume <session>` on the subscription. The idea tools
  (`Get-Tools` in `idea-turn.ps1`: `submit_brief`, the spec tools, etc.) are exposed either as a
  small stdio MCP server (`Factory.Mcp`) or as `factory` commands the session may run. Recommendation:
  MCP, which keeps tool schemas typed and needs no shell permission.
- **Cloud mode:** the Messages API on Foundry (`https://<resource>.services.ai.azure.com/anthropic`)
  through `Azure.Identity`, with the same tool definitions.
- The session files under `sessions/<S>/` keep the contract's layout, so the dashboard is
  unchanged.
- Contract first: "Idea sessions" changes from "Claude API (key in `.env`)" to "follows the run
  mode". CLAUDE.md and the docs drop the API key.

## Tests

`idea-turn.Tests.ps1` passes both ways; `anthropic-stub.ps1` covers cloud mode and `tool-stub.ps1`
covers local. One live idea session per mode ends in an approved spec.

## Done when

`.env` no longer needs `ANTHROPIC_API_KEY` for anything.

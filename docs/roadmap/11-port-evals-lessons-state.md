# 11: Port evals, lessons and state admin

**Status:** not started. **Branch:** `feature/11-port-evals`. **Depends on:** 04.

## Commands

`eval`, `feedback`, `lesson` (incl. the build.md template-gap compile and the `- None.` filter),
`lesson-mark`, `state-migrate`, `state-pull`, `prune`, and `allow-ip` (through `az`, or the ARM API
with `Azure.Identity`; the last-added IP is recorded in the OS app-data folder, not
`%LOCALAPPDATA%` literally).

## Tests

Their `Describe` blocks pass both ways, local and azure.

## Done when

Every `factory.ps1` command is forwarded. The forwarding list equals the command table.

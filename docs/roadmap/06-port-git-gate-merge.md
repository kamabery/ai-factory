# 06: Port the git commands, gate and merge

**Status:** not started. **Branch:** `feature/06-port-git`. **Depends on:** 05.

## Commands

`checkout` (creates the repo if needed, clones or reuses `<work>/jobs/<id>`, fetches, points
`factory/base`), `publish` (push, open the PR), `gate`, `gate-check` (with its per-sha cache and
`(cached at <sha>)`), `provenance`, `merge` (green gate, clean tree, starts a detached `destroy`),
`sync-prs`, `set-cost`, `next` (incl. `-NeedsCanary`), and the build checks (scope check, base
moved).

## Notes

- Git is run as a process (`git` from PATH), not through a library: the contract's behaviour is
  git's. Only the factory pushes, using the credential from the GitHub client (05).
- The work root is `FACTORY_WORK`, else the OS temp folder plus `ai-factory` (`Path.GetTempPath()`,
  not `%TEMP%`). The contract text changes to say that.
- The dirty-tree check keeps its stricter-than-git semantics and its error message, which names what
  is dirty (`docs/BRANCHING-STRATEGY.md`, gap 6).
- "Start detached" for `destroy` becomes a child process that survives the parent on all three OSes.
  Test that on Linux and macOS in CI.

## Done when

The gate, merge, publish and checkout `Describe` blocks pass both ways on three OSes.

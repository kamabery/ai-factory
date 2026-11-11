# 06: Port the git commands, gate and merge

**Status:** built on its branch (2026-11-11), Windows and Linux proven, macOS not. **Branch:**
`feature/06-port-git`. **Depends on:** 05. Nine commands forwarded: `checkout`, `publish`, `gate`,
`gate-check`, `provenance`, `merge`, `sync-prs`, `set-cost` and `next`. The build checks were already
ported with `set-stage` (04), and the contract already said `<system temp>`. Proven locally:
- Windows: .NET 533 unit, 52 integration (Core 99.7%, Store 97.6%, Cli 98.9%); fourteen deliberate
  breaks, one per command rule, each turned a test red (two stayed green at first and got the tests
  they lacked). Pester `-Tag Ported` both ways, local and azure; `-Impl dotnet` 437 local, 235 azure;
  the plain run 437 local, 235 azure; Playwright on .NET 41.
- Linux (WSL Ubuntu 20.04, `factory/tests/linux/run.sh`): .NET 533 unit, 52 integration; `-Tag Ported`
  308 of 312 local, 276 of 282 azure (2 skipped). Every block this spec names passes both ways. The two
  tests still failing fail the same on PowerShell and .NET, in earlier specs' blocks: `purge` "removes a
  work dir that merge or discard could not delete" (Linux deletes an open file, so the lock it simulates
  never holds) and `targets` "the bootstrap spec's ui gate runs npm ci only when ..." (not yet
  diagnosed). Both are follow-ups for whoever next needs the whole suite on Linux.
- macOS: not run; there is no Mac to run it on.

What changed beyond the port:
- The board session can release its lock around a long step (`IBoardSession.Unlocked`), as
  `Exit-BoardLock` / `Enter-BoardLock -1` did; the relock waits as long as it takes (the lease 600 s).
- `destroy` (spec 09) still runs in PowerShell: `merge` and `sync-prs` start `factory.ps1 destroy`
  detached. On Linux and macOS it goes through `/bin/sh` with its stdio on /dev/null; a test proved
  the first launcher, a plain child, held its caller's output 8 s.
- `runner.ps1` treats a refused `set-cost` as an error finishing the job. In-process, PowerShell's
  `[double]::Parse` threw into the runner's catch; forwarded, the refusal is an exit code, which the
  runner had dropped. The contract's runner step says so now.
- The Pester suite runs on Linux: `fixtures/portable.ps1` drops `-WindowStyle` off Windows and gives a
  started child its own stdio (under `Start-Job` an inherited stdout is the job's channel), the gate
  tests echo with `/bin/echo` there, and the checkout test's git hook is made executable.

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
  Test that on Linux and macOS (by hand: the repo has no CI).

## Done when

The gate, merge, publish and checkout `Describe` blocks pass both ways on three OSes.

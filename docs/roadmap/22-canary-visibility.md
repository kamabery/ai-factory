# 22: A Canary column, and the canary shown while it runs

**Status:** planned (2026-11-20), after 18. **Branch:** `feature/22-canary-visibility`. **Depends on:** 17, 18.

## Why

On 2026-11-20 the cloud factory's first deployable job after the 0.3.8 release (J-003, kamabery/booklist) sat in
Approved for over 10 minutes with no sign of why. The runner was running the 0.3.8 canary first, as the contract's
"The gate" says, but the dashboard showed nothing. `CanaryBanner` shows "Deploy canary running" only when
`canary.json` says `running`. In cloud mode the canary runs in the runner's container, and `canary.json` reached
Blob only when the canary finished, so the dashboard never saw the `running` state. Before the job was approved,
nothing said a canary was due either: the canary is lazy, and **Run canary** only appears after a failure.

## Decisions (grilled 2026-11-20)

| Topic | Decision | Not chosen |
| --- | --- | --- |
| Where it shows | **The existing top banner**, made to work in cloud mode. | A note on the waiting job's card; the job's modal; spec 21's session stepper |
| Detail | **Phase and link**: `Deploy canary (0.3.8) · deploying 2/3 · 7 of ~15 min · Actions run ↗`, and a line naming the jobs waiting for it. Phases: scaffold → deploying 1/3, 2/3, 3/3 → smoke → sign-in → tearing down. The ~15 min comes from recent passes. | Running and elapsed time only; a live log panel |
| Plumbing | **The canary writes `canary.json` at start and at each phase change, and pushes it** (blob-backed state), not only at the end. Every board reads the same file, local or cloud. | The server reading GitHub Actions directly (it sees only the deploy, not smoke, sign-in or teardown) |
| Due | **A quiet "canary due" chip** once the fingerprint has no pass: "canary due: the next deployable job waits in Canary for it (~15 min)". | Saying nothing until it runs; running it eagerly after every release |
| The gate (revised 2026-11-20) | **A real `canary` stage and a Canary column** between Approved and Build. Only a deployable job taken while the current version has no canary pass enters it: approved → canary → build. Bootstraps, targets with nothing to preview, and jobs after a pass go straight to Build. **Canary first**: the build starts only after the pass. On a failure, the job **stays in Canary, shown red**, with the error and **Run canary** on its card (and in the banner); a pass moves it to Build. | Building beside the canary (decided, then dropped the same day); a dashboard-only column derived from `canary.json`; sending a failed job back to Approved; every deployable job crossing the column |

## Scope

Contract first (`docs/FACTORY-CONTRACT.md`, "Canary" and "Dashboard API"):

- `canary.json` gains `phase` (`scaffold` | `deploy-1` | `deploy-2` | `deploy-3` | `smoke` | `signin` | `teardown`,
  null when finished), `version` (the factory package version the fingerprint names, for the banner), and `run`
  (the deploy workflow run's URL, once dispatched).
- The canary saves `canary.json` at start and at each phase change. With shared files (`azure`, or `sql` with
  `FACTORY_BLOB_URL`), each save pushes `canary.json` right away, not at the end of the command.
- **Due:** `GET /api/canary` (or the board's existing canary read) adds `due: true` when `canary.json` has no pass for
  the current fingerprint and no canary is running. The SPA shows the chip; it disappears once a canary starts.
- **Typical duration:** the median of the last passes' `finishedAt - startedAt`, from `canary.json` history (or a
  fixed 15 min until there are three passes). Show it as "N of ~M min".
- **The `canary` stage** ("Stages", "Transitions", "Canary", "The gate"): a new stage between `approved` and
  `build`. When `next` picks a deployable job and `canary.json` has no pass for the current fingerprint, the runner
  moves it `approved → canary` (`set-stage`, `-By runner`), runs `canary`, and on a pass moves it `canary → build`
  and builds it. On a failure the job stays in `canary` and the runner exits. **Run canary** (`canary -Force`, or
  `-Retry` in the cloud) runs it again, and the next pass takes every job in `canary` to `build` in board order. One
  canary per fingerprint: other deployable jobs taken meanwhile join the stage and wait on the same run. A drag out
  of Canary is refused, except Discard and back to Approved (which leaves the canary running). `status`, events and
  the board export carry the stage like any other.
- **The column:** the board gains **Canary** between Approved and Build. A card there shows `canary running N/~M min`
  (phase from `canary.json`), or, after a failure, red with the canary's error, its Log and **Run canary**.
- **SPA:** `CanaryBanner` renders the running state from the new fields, and the jobs waiting for it are the
  jobs in the `canary` stage. A `CanaryDueChip` sits by the run-mode chip.

## Tests

Seen red before green, per `docs/TESTING.md`:

- Unit: the `canary` stage's transitions (allowed and refused drags); the runner moves a deployable job with no pass to `canary`, a bootstrap or nothing-to-preview job straight to `build`; a pass moves every `canary` job to `build` in order, a failure leaves them; one canary per fingerprint; canary phase saves in order; each pushes when files are shared and doesn't when they're not; `due` for
  pass/failed/running/none against the fingerprint; the typical-duration rule.
- Vitest: the banner for each phase (text, elapsed time, link, waiting jobs) and the due chip.
- Playwright `@smoke`: the Simulator puts a job in the `canary` stage with a running canary; the Canary column and the banner show it, then a pass moves it to Build. A failed-canary card shows red with Run canary.
- Live: after the next image release, the cloud banner shows the phases while the canary runs.

## Done when

Approving the first deployable job after a release puts it in the Canary column within a minute, and shows the canary's phase, elapsed time and Actions link in the
banner within a minute of the canary starting, in both local and cloud modes. The "canary due" chip is visible
before anything is approved.

# 22: A Canary column, the canary shown while it runs and why it is due, no Backlog column, and a New repo idea

**Status:** planned (2026-11-20; Backlog removal added 2026-11-21; canary reasons, the New repo idea and local-first
proofs added 2026-11-22), after 21. **Branch:** `feature/22-canary-visibility`. **Depends on:** 17, 18, 21, 23.

## Why

On 2026-11-20 the cloud factory's first deployable job after the 0.3.8 release (J-003, kamabery/booklist) sat in
Approved for over 10 minutes with no sign of why. The runner was running the 0.3.8 canary first, as the contract's
"The gate" says, but the dashboard showed nothing. `CanaryBanner` shows "Deploy canary running" only when
`canary.json` says `running`. In cloud mode the canary runs in the runner's container, and `canary.json` reached
Blob only when the canary finished, so the dashboard never saw the `running` state. Before the job was approved,
nothing said a canary was due either: the canary is lazy, and **Run canary** only appears after a failure.

Nor can anyone tell **when or why** a canary is needed (Kurt's questions, 2026-11-22). It is not tied to the run
mode (it runs no LLM, so it spends no Foundry or subscription tokens), nor to new apps (a bootstrap never deploys),
nor to which board runs it. It is tied to deploys being on (`HCP_ORG`) and to one fingerprint: a hash over four
groups of inputs, namely the deploy code (or the package version in a tool install), the templates (`IaC/`,
`deploy.yml`, the template pack), the HCP variable set, and the canary's own settings. When the hash changes, nothing
says which group changed or which jobs will wait.

And **scaffolding a new repo reads as the same thing as building an idea.** Since 21 a new-target session runs
Brief → Scaffold → Specs → Build as one idea: one grill covers both the app and its first feature, and the bootstrap
card and the feature cards share the idea. Kurt (2026-11-22): separate them in the idea phase. A **New repo** idea
grills the app and scaffolds it, and its result is a product document in the new repo listing the features to build.
Each feature is then started on its own, with **Implement first feature**.

## Decisions

Grilled 2026-11-20, 2026-11-21 and 2026-11-22.

| Topic | Decision | Not chosen |
| --- | --- | --- |
| Where it shows | **The existing top banner**, made to work in cloud mode. | A note on the waiting job's card; the job's modal; spec 21's session stepper |
| Detail | **Phase and link**: `Deploy canary (0.3.8) · deploying 2/3 · 7 of ~15 min · Actions run ↗`, and a line naming the jobs waiting for it. Phases: scaffold → deploying 1/3, 2/3, 3/3 → smoke → sign-in → tearing down. The ~15 min comes from recent passes. | Running and elapsed time only; a live log panel |
| Plumbing | **The canary writes `canary.json` at start and at each phase change, and pushes it** (blob-backed state), not only at the end. Every board reads the same file, local or cloud. | The server reading GitHub Actions directly (it sees only the deploy, not smoke, sign-in or teardown) |
| Due | **A quiet "canary due" chip** once the fingerprint has no pass: "canary due: the next deployable job waits in Canary for it (~15 min)". | Saying nothing until it runs; running it eagerly after every release |
| Why it is due (Kurt, 2026-11-22) | **Show the reason, keep the gate.** `canary.json` records a hash per input group, and the chip, the banner and the Canary card name what changed since the last pass: `templates changed since pass #32`, `factory 0.4.1 → 0.4.2`, or `no pass on record`. Every deployable job still waits. | Gating per group, so a templates-only change holds only jobs on newly scaffolded targets (a follow-up below); explaining it in the docs only |
| The gate (revised 2026-11-20) | **A real `canary` stage and a Canary column** between Approved and Build. Only a deployable job taken while the current version has no canary pass enters it: approved → canary → build. Bootstraps, targets with nothing to preview, and jobs after a pass go straight to Build. **Canary first**: the build starts only after the pass. On a failure, the job **stays in Canary, shown red**, with the error and **Run canary** on its card (and in the banner); a pass moves it to Build. | Building beside the canary (decided, then dropped the same day); a dashboard-only column derived from `canary.json`; sending a failed job back to Approved; every deployable job crossing the column |
| No Backlog column (Kurt, 2026-11-21) | **The board loses its Backlog column. Specs are approved from the Ideas column**, on the idea's (or its session's) card or in its opened card, and go straight to **Approved**, or to **Canary** when the runner takes them while a canary is due. A spec that is not approved yet stays with its idea in Ideas. | Keeping Backlog as a holding column; approving only by drag |
| New repo idea (Kurt, 2026-11-22) | **Its own idea kind.** Its grill covers the repo name, the scaffold options, the app's purpose and users, its Home, and the features to build, and ends in a confirmed `docs/PRODUCT.md`, not a brief. It writes no specs of its own; its one job is the bootstrap. | One idea that scaffolds and specs its first feature (21's flow) |
| PRODUCT.md | **Purpose, users, the Home decision, and 1 to 10 features in build order**, each a `###` heading and a paragraph. It is the app's roadmap. | A single first-feature doc; a factory-ready spec committed to docs |
| Who writes it | **The factory commits it** (`docs: product`) into the bootstrap's clone before the builder starts; the bootstrap spec says to leave it as it is, and the reviewers check it is unchanged. Deterministic, no agent rewording. | The builder writing it from the confirmed text |
| Board place | **One repo card in Ideas**, marked New repo, with steps Grill → Scaffold → Review → Merged. The bootstrap job still crosses Build and the review stages as a job. | A Repos column; a button on the Done bootstrap card |
| The button | **Implement first feature** appears once the scaffold merges (session Merge, a drag to Done, or a GitHub merge PR sync records). It then offers **Implement \<next feature\>** in list order, the first feature with no idea yet, until every feature is started or the human dismisses the card. The list is read from PRODUCT.md on the target's `main`, so edits made there show up. | Offering it at Ready for review; offering the first feature only and leaving the rest to New idea |
| What the button starts | **A short-grill idea session on the repo**, seeded with the feature's text and the real code: the grill asks only what PRODUCT.md and the scaffold leave open, then the spec-writer writes 1 to 5 specs as today. | Straight to the spec-writer, the doc counting as the brief |
| Tracking | **On the board**: the repo idea maps each feature heading to the `I-NNN` that started it. The factory never edits PRODUCT.md after its first commit. | Each feature job ticking its feature in PRODUCT.md |
| Home (spec 23) | **The New repo grill asks about Home and records it in PRODUCT.md**; the first feature's spec set must carry the `- Home:` line, and `submit_specs` refuses it otherwise. 23's rule keeps its text and refusal; its key moves from "the idea scaffolded its target" to "the first feature of a scaffolded repo". | The bootstrap applying Home itself, so it stops being template-only |
| CLI door | **Mirrored**, as the contract's two doors require: `/factory:idea <new owner/name>` runs the New repo grill and stops at the bootstrap; `/factory:idea <owner/name> --feature <k>` (and `factory idea <owner/name> --feature <k>`) starts feature k. | New repo in the dashboard only |
| Older repos | **No repo card** for booklist, pantry or todoapp-two, which were scaffolded before 22; they keep using New idea. | A card for any target whose `main` has a PRODUCT.md |
| Proofs (Kurt, 2026-11-22) | **Live work runs on the local board.** The cloud board gets the release applied and the dashboard's Playwright `@smoke` set run against its URL: no live cloud job, no Foundry spend. A roadmap rule from 22 on (README, "Both boards at the end"). | A cheap live cloud job as well; a health check only |

## Scope

Contract first (`docs/FACTORY-CONTRACT.md`, "Canary" and "Dashboard API"):

- `canary.json` gains `phase` (`scaffold` | `deploy-1` | `deploy-2` | `deploy-3` | `smoke` | `signin` | `teardown`,
  null when finished), `version` (the factory package version the fingerprint names, for the banner), and `run`
  (the deploy workflow run's URL, once dispatched).
- The canary saves `canary.json` at start and at each phase change. With shared files (`azure`, or `sql` with
  `FACTORY_BLOB_URL`), each save pushes `canary.json` right away, not at the end of the command.
- **Why it is due:** `canary.json` also gains `inputs` (`{ "deploy", "templates", "varset", "settings" }`, each that
  group's sha256) and `passes` (the last 5 passes, newest first, each `{ n, version, inputs, startedAt, finishedAt }`).
  `CanaryCheck.Fingerprint` (`src/factory/Factory.Store/CanaryCheck.cs`) already builds its lines in exactly these
  four blocks, so it hashes each block as well as the whole. **The overall fingerprint stays byte-identical**, so a
  pass recorded before 22 still counts.
- **Due:** `GET /api/canary` (or the board's existing canary read) adds `due: true` when `canary.json` has no pass for
  the current fingerprint and no canary is running, and `reasons`: the groups whose hash differs from the newest pass
  (`deploy` as the version pair in a tool install, such as `factory 0.4.1 → 0.4.2`), or `no pass on record`.
  `due` is never true while deploys are off (no `HCP_ORG`, so no job is deployable). The SPA shows the chip with the
  reason, such as `canary due: templates changed since pass #32 · the next deployable job waits in Canary (~15 min)`;
  it disappears once a canary starts.
- **Typical duration:** the median of `finishedAt - startedAt` over `passes`, or a fixed 15 min until there are three
  passes. Show it as "N of ~M min".
- **The `canary` stage** ("Stages", "Transitions", "Canary", "The gate"): a new stage between `approved` and
  `build`. When `next` picks a deployable job and `canary.json` has no pass for the current fingerprint, the runner
  moves it `approved → canary` (`set-stage`, `-By runner`), runs `canary`, and on a pass moves it `canary → build`
  and builds it. On a failure the job stays in `canary` and the runner exits. **Run canary** (`canary -Force`, or
  `-Retry` in the cloud) runs it again, and the next pass takes every job in `canary` to `build` in board order. One
  canary per fingerprint: other deployable jobs taken meanwhile join the stage and wait on the same run. A drag out
  of Canary is refused, except Discard and back to Approved (which leaves the canary running). `status`, events and
  the board export carry the stage like any other.
- **The column:** the board gains **Canary** between Approved and Build. A card there shows `canary running N/~M min`
  (phase from `canary.json`) and the reason it runs, or, after a failure, red with the canary's error, its Log and
  **Run canary**.
- **SPA:** `CanaryBanner` renders the running state and the reason from the new fields, and the jobs waiting for it
  are the jobs in the `canary` stage. A `CanaryDueChip` sits by the run-mode chip.

- **No Backlog column** (grilled 2026-11-22): the `backlog` stage stays in the
  contract and the CLI, as where an unapproved spec lives, but the board has no column for it. Each feature idea's
  card in Ideas (and its session card, and spec 21's Specs and Build phases) lists its specs still in `backlog`, each
  with **Approve** (the same `approve` the drag runs) and **Edit** (today's backlog editing), so a spec is edited and
  approved where it was decided. A repo card lists none: it shows only its scaffold and its next feature. Approving
  moves a spec to Approved, and the runner takes it on to Canary or Build as above. Settled (Kurt, 2026-11-22):
  - **A spec with no idea** (a `new-job`, or an idea record that is gone) gets **its own card in Ideas**, styled as a
    spec, with Approve, Edit and Discard. Not chosen: one "Loose specs" group card.
  - **Not yet keeps the idea open.** A session with a spec left in `backlog` does not run `idea-done` when every
    spec is decided; the idea stays `drafting`, its card lists the waiting specs, and it closes (`idea-done`) only
    when the last of them is approved or discarded. Not chosen: showing a done idea while it has backlog specs;
    dropping Not yet.
  - **A refused approval shows on its spec's row, sticky**, under the spec on its idea or repo card, until the next
    try succeeds, as well as spec 21's large sticky toast. A refused bootstrap shows on the repo card's Scaffold
    step with Approve to retry. Not chosen: the toast only.
  - **Out of Approved:** the contract has no `approved → backlog` move, and 22 adds none. Out of Canary, 22 adds
    `canary → approved` and `canary → discarded`, both human.
  - **PRODUCT.md missing or with no feature left** on the target's `main`: the repo card says so (`docs/PRODUCT.md
    on main has no feature to start`, or the read error) and offers Dismiss only; New idea still works on the repo.
    Not chosen: a board copy to fall back on.

- **The New repo idea** (contract "Targets", "Ideas" and "Idea sessions" first; the names below are proposals, to
  confirm against the contract at the start of 22):
  - The idea gains `kind: "repo"` (absent means a feature idea) and `features: [{ "heading", "idea": "I-NNN" | null }]`.
    A feature idea started from it records `from: "I-NNN"` and `feature: "<heading>"`.
  - New idea lists existing repos only. **+ Add new** becomes its own **New repo** entry, and its session's grill
    ends in `submit_product` in place of `submit_brief`: the PRODUCT.md text, which the tool answers as an error when
    it has no `## Home` decision or fewer than 1 or more than 10 features. Confirm runs `new-idea` with `-Scaffold`
    (and the product file), creates and approves the bootstrap, and starts the runner, as 21's Confirm does today.
  - Before the builder starts, the factory commits PRODUCT.md into the bootstrap's clone as `docs/PRODUCT.md`
    (subject `docs: product`), so the scaffold PR carries it. `factory/templates/bootstrap.md` gains an acceptance
    criterion that PRODUCT.md is unchanged from that commit, and its Scope lists it as In.
  - A repo session's stepper is Grill → Scaffold → Review → Merged; its card's state says whose move it is, as
    today. When the bootstrap merge is recorded, the repo idea moves to `drafting`, reads PRODUCT.md from the
    target's `main`, and the card shows **Implement first feature** (later, **Implement \<heading\>**) and
    **Dismiss**. The server's sweep no longer starts a spec turn after a bootstrap merge.
  - **Implement** runs `new-idea` on the repo with the feature's text (`-From I-NNN -Feature <heading>`), records it in
    the repo idea's `features`, and opens an existing-target session seeded with that text and PRODUCT.md: brief →
    specs → build, as for any existing repo. When every feature has an idea, or on **Dismiss**, the repo idea goes
    `done` (`idea-done`).
  - **Home:** the spec-writer rule and the `submit_specs` / `revise_spec` refusal of spec 23 apply to the first
    feature idea of a repo idea (the one whose `from` repo idea has no other feature started), with the Home answer
    taken from PRODUCT.md.
  - **CLI:** `/factory:idea <new owner/name>` grills to PRODUCT.md and stops at the bootstrap;
    `/factory:idea <owner/name> --feature <k>` (`factory idea <owner/name> --feature <k>`) starts feature k from the
    target's PRODUCT.md, refusing when it has no such feature or the feature already has an idea.
  - Repos scaffolded before 22 (booklist, pantry, todoapp-two) get no repo card.

- **Documentation** (Kurt, 2026-11-21), part of the spec, not a follow-up. Updated in the same PR as the code:
  - **README.md:** how a spec is approved from Ideas, where "not yet" leaves it (today line 58 sends it to
    Backlog), the Canary column in the board's walk-through, and the two kinds of idea: New repo (to PRODUCT.md and a
    merged scaffold) and a feature, started from a repo card or New idea.
  - **docs/TERMINOLOGY.md:** the idea-to-job diagram and the card's path (Backlog → Approved → Building ...) lose
    the Backlog column; the repo card, PRODUCT.md and **Implement** join it; **Canary** gains the stage and column,
    and a plain account of what the canary is and why deployable jobs wait for it: it deploys a fresh scaffold of the
    current templates through the same path a job's preview takes, so a broken template or deploy change fails one
    throwaway run, not a real job after its tokens are spent (J-019's three attempts went on three platform bugs).
    Its limits too: one scaffold (Postgres + React + sign-in, so a SQLite-only bug like template-projects PR #10's
    gets past it), the shared platform (only `-Full` proves a from-scratch stack), and never an existing target's own
    IaC.
  - **When a canary runs**, a short table in TERMINOLOGY and DEPLOY: deploys off, never; the run mode and the board
    make no difference; a bootstrap never deploys, so never waits; once per fingerprint, not per job; and the four
    input groups with who each affects (deploy code and the variable set, every target's previews; the templates,
    mostly targets scaffolded after the change, since a target deploys its own committed copy of `IaC/` and
    `deploy.yml`).
  - **docs/DEPLOY.md:** a short "The canary" section for the operator: when it runs (the table above), what the
    Canary column, banner and due chip show, how to read the chip's reason, what a red Canary card means, and
    **Run canary** / `canary -Retry` in the cloud.
  - **docs/CLOUD.md:** the release step says a new image forces a canary, that the first deployable job then waits in
    Canary, and that a release is checked with the Playwright `@smoke` set against the deployed URL.
  - **docs/BUILD-GUIDE.md:** its backlog wording, where it describes the board rather than the `backlog` stage.
  - The contract changes come first, as for every spec.

## Tests

Seen red before green, per `docs/TESTING.md`:

- Unit: the `canary` stage's transitions (allowed and refused drags); the runner moves a deployable job with no pass to `canary`, a bootstrap or nothing-to-preview job straight to `build`; a pass moves every `canary` job to `build` in order, a failure leaves them; one canary per fingerprint; canary phase saves in order; each pushes when files are shared and doesn't when they're not; `due` for
  pass/failed/running/none against the fingerprint; the typical-duration rule.
- Unit, why it is due: per-group hashes, with the overall fingerprint unchanged for the same inputs; `reasons` for a
  change in each single group, in two groups, with no pass on record, and the version pair in a tool install; no
  `due` with deploys off; `passes` capped at 5, newest first.
- Unit, New repo: `submit_product` refuses 0 or 11 features and a missing Home; the factory's `docs: product` commit
  lands before the build; a bootstrap merge offers the first feature and starts no spec turn; **Implement** records
  the feature and the next one offered is the first with no idea, matched by heading; the last feature or Dismiss
  closes the repo idea; the Home refusal applies to a repo's first feature idea and not to its second; the CLI's
  `--feature k` and its refusals.
- Vitest: the banner for each phase (text, elapsed time, link, waiting jobs, reason) and the due chip with its reason;
  the repo card's steps, its Implement and Dismiss buttons.
- Playwright `@smoke`: the Simulator puts a job in the `canary` stage with a running canary; the Canary column and the banner show it, then a pass moves it to Build. A failed-canary card shows red with Run canary. The Simulator shows a merged repo card; Implement first feature opens a session seeded with the feature's text.
- No Backlog column: Vitest, the board has no Backlog column and a feature idea's card lists its `backlog` specs
  with Approve and Edit; Playwright `@smoke`, a spec is approved from its idea card and lands in Approved (or Canary
  when one is due), with no drag; unit, whatever the grill settles for specs with no idea.
- Live, on the local board: a New repo idea scaffolds a repo whose PR carries PRODUCT.md, its merge offers
  Implement first feature, and that feature's first deployable job waits in Canary with the banner's phases when a
  canary is due.
- Cloud: after the image release, the Playwright `@smoke` set passes against the deployed board's URL. No live cloud
  job.

## Done when

On the local board: a New repo idea grills to a PRODUCT.md, scaffolds the repo with PRODUCT.md in its first PR, and
after the merge its card offers **Implement first feature**, whose session writes a spec set that decides Home.
Approving the first deployable job after a release puts it in the Canary column within a minute, and shows the
canary's phase, elapsed time and Actions link in the banner within a minute of the canary starting. The "canary due"
chip is visible before anything is approved and names what changed since the last pass; a board with deploys off
never shows it. The board has no Backlog column: a feature idea's specs are approved from the Ideas column and go
straight to Approved or Canary. The cloud board runs the released image and passes the Playwright `@smoke` set
against its URL. The README, TERMINOLOGY, DEPLOY, CLOUD and BUILD-GUIDE docs describe the board as it now is, say
what the canary is, when it runs and why jobs wait for it, and describe the two kinds of idea.

## Progress (2026-11-22)

Built on `feature/22-canary-visibility`: canary reasons (`0d6af9f`, `b852a2e`), phases (`849d339`), the Canary stage
and column (`0c60468`), its Playwright `@smoke` (`44866e7`), and no Backlog column (`de13b60`). Next: the New repo
idea, then the docs list and the proofs.

Where the build differs from the plan above:
- **The Canary `@smoke` serves its board and `canary.json` from the test**, not the Simulator: the Playwright
  dashboard runs with deploys off, so its watchdog's runner takes a Canary job straight to Build.
- **No `canary → approved` drag.** A job dragged back would wait in Approved just the same; Discard is the way out.
- **A refused bootstrap approval** shows as a spec row on its idea's card for now; the repo card's Scaffold step
  (with Approve to retry) comes with the New repo idea.
- **Not yet keeps the idea open in the CLI**: `idea-done` leaves an idea with `backlog` specs `drafting` with
  `closeWhenDecided`, and `approve`/`discard` of the last one closes it, so the server is unchanged.

## Follow-ups (not built in 22)

- **Gate by input group.** A templates-only change would hold only jobs on targets scaffolded after it, plus jobs
  whose build pulls current template code into an older target (`aspire-addon add`, the devops agent's author pass,
  which gives a target with no HCP-ready infrastructure its IaC). It needs a rule for spotting those jobs, so it waits
  until the reasons have been seen in use.
- **Two boards, one canary?** Unverified: a local board and the cloud board keep separate `canary.json` files, so
  the same fingerprint may be canaried once on each, both on the shared platform. Check it once both boards run 22.

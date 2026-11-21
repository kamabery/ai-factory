# 21: New-repo idea sessions, one flow the user can follow

**Status:** done, merged in PR #33 (2026-11-21). One session with a phase stepper (Brief → Scaffold → Specs → Build); confirming the brief approves the bootstrap and starts the runner; **Merge** in the session; a merge starts the specs with no click; two notices (toast + browser notification); the card says whose move it is beside Abandon. The stuck Approve modal was reproduced locally, then removed with its button. Folded in: sticky refusal toasts, Rework needs a note, Copy on Output, and a Scope line naming a path after other words is refused when written or approved (cloud J-005). Proven on a local board, then in the cloud on ai-factory 0.4.0: kamabery/pantry, J-004 bootstrap ($1.83, eval good) and J-005 with a live SQLite preview ($5.12, eval mixed), both merged; canary passed. Tests: unit 1152, integration 200 + 1 skipped (Windows), WSL 1152 / 197 + 4 skipped, Vitest 93, Playwright 43 (smoke 6). **Branch:** `feature/21-new-repo-session-flow`. **Depends on:** 17, 18.

## Why

Cloud I-002 (session S-002, `kamabery/booklist`) showed the problem. An idea for a new repo runs in two
phases, scaffold then features, but the dashboard never says so. The only hint up front is one line in the
New idea panel. After the brief, the session asks for an **Approve bootstrap** click, and that click left the
modal stuck (2026-11-20, 0.3.8). **Write specs** stays disabled, and only a tooltip says why. Nothing tells
the user when the bootstrap is ready to merge, or when it has merged. The user has to know to go back into
the same session.

## Decisions (grilled 2026-11-20)

| Topic | Decision | Not chosen |
| --- | --- | --- |
| Mental model | **One session, with a phase stepper**: Brief → Scaffold (J-NNN) → Specs → Build. The current phase is lit, and the waiting reason is plain text, not a tooltip. | Two linked sessions; hiding the bootstrap; copy only |
| Stepper reach | **Through Build**: once specs are approved, the last phase lists the feature jobs and their stages (and the preview). | Existing-repo sessions using the same stepper; a compact stepper on the session card |
| Bootstrap approval | **Confirming the brief and scaffold is the approval**: the bootstrap job goes straight to Approved. **The human still merges** the scaffold. | Explicit Approve click (today); auto-merge on a green gate |
| Where the merge happens | **In the session too**: when the bootstrap is Ready for review, the Scaffold phase shows its PR and a **Merge** button, which runs the same `factory merge` as dragging to Done. The board drag still works. | Board only |
| After the merge | **The spec-writer turn starts on its own**: the merge does what **Write specs** does today. | Waiting for a click |
| Telling the user | **Two moments**: (1) "J-NNN is ready for review: merge the *repo* scaffold"; (2) "Specs for I-NNN are ready to approve". Each one changes the session card's state, shows a toast or banner on the board, and sends a **browser notification**. | Pinging every phase change; specs-ready only |
| First hint | **In the New idea panel**: picking **+ Add new** shows "A new repo is set up first by a bootstrap job. Its feature specs are written after you merge it." | Saying it in the grill; stepper only |
| Cloud gap | **Documented, not engineered**: the session's Merge, or a drag to Done, starts the specs at once. A merge on GitHub is picked up by PR sync the next time the dashboard is open (it scales to zero), and the stepper says so. | The runner also watching for merged bootstraps |

## Scope

Contract first (`docs/FACTORY-CONTRACT.md`, "Idea sessions", "Dashboard API"):

- **Brief confirmation:** `POST /api/sessions/<S>/brief` on a new target runs `new-idea`, then `approve <bootstrap>`,
  then starts the runner. If `approve` refuses, the bootstrap stays in Backlog with the message, as a refused spec
  approval does today.
- **The session's phase**, worked out on the server from the session and the board, never stored: `brief` | `scaffold`
  (with the bootstrap's stage) | `specs` | `build` (with the jobs' stages). `GET /api/sessions` and the session's
  own read return it.
- **Starting the specs on merge:** when `merge` (the drag or the session's Merge) or `sync-prs` records the
  bootstrap as Done, and its idea turns `drafting`, the server runs what `write-specs` does. Only once: a session
  that already has specs, or a turn running, is left alone. While the dashboard is closed, a GitHub merge waits
  for the next PR sync.
- **Notices:** the server records the two moments, so a reload doesn't repeat them. The SPA shows a toast or banner,
  and a browser `Notification` when the user has granted permission. Permission is asked once, from the New idea
  panel when **+ Add new** is picked, never on page load.
- **SPA:** a `SessionStepper` component in the session modal; the Scaffold phase's PR link and Merge button; the
  session card's state text (`waiting for bootstrap J-NNN (in review)` → `Ready: merge the scaffold` →
  `writing specs` → `specs ready to approve`); the New idea panel's note. The **Approve bootstrap** button goes,
  and with it the stuck-modal path. Still reproduce that bug locally first, so a test proves it gone
  ([memory: approve-modal-stuck-followup]).

## Tests

Seen red before green, per `docs/TESTING.md`:

- Unit (`Factory.UnitTests`): the phase for every session/board shape; brief → approve; merge → one spec turn
  (not two, not while a turn runs); notices recorded once.
- Vitest: the stepper's phases and texts; the card states; the notification permission flow (mocked `Notification`).
- Playwright: the Simulator gains a new-repo idea that goes brief → scaffold → merge → specs. `@smoke` adds one case:
  confirm the brief, see Scaffold lit, merge in the session, see the specs arrive with no click. The stuck-modal
  repro is a case of its own.
- Built and checked against a **local board** (`factory serve` and the Simulator) before any cloud release.

## Done when

A new-repo idea runs from New idea to approved specs with one approval (the brief), one merge, and the dashboard
saying at every step what is happening and what, if anything, the user must do. Proven locally, then once in the
cloud on a real new repo.

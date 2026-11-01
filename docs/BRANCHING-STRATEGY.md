# Branching strategy: the integration loop (proposal)

Status: **proposal**. Nothing here is in `FACTORY-CONTRACT.md` yet. It is adopted by editing the
contract first, then the scripts, with Pester and Playwright seen red before green.

One piece landed differently (J-022): gap 2 is narrowed by the builder, not an `integrate`
command. `checkout` fetches and points `factory/base` at the base branch, and each round's builder
starts with `git merge --no-edit factory/base` and resolves conflicts itself. A base that moves
while a job sits in Ready is still caught only by a rework.

**Goal: a card in Ready for review always merges clean.** The factory owns every git write and
loops (integrate, resolve, gate) until the merge is a formality. A human drag to Done should never
find a conflict, an ungated tree, or a stale base.

## Why: J-009 and what it exposed

J-009 reached Ready with a green gate on `92c1622`. The drag to Done was refused: `merge-blocked`,
"release checkout is dirty".

- The only dirty path, `src/ui/react/src/routeTree.gen.ts`, has the same blob as `HEAD` (`7968e03`).
  `autocrlf=true` checked it out as CRLF, and the TanStack Router plugin rewrote it as LF when the UI
  ran on `main`. `git status --porcelain` reports ` M`. `git diff --quiet HEAD` returns 0.
  **The factory's dirty check is stricter than git's notion of a content change.**
- This is fixed at the source: `template-projects/base-project/src/ui/react/.gitattributes` pins the file to LF
  (repro red, then green). Existing targets don't get it.

The same analysis found these structural gaps:

| # | Gap | Effect |
| --- | --- | --- |
| 1 | The gate attests `factory/<id>`'s head, not the merge result | A merge after `main` moves lands an untested tree |
| 2 | Jobs branch from `main` when they start building, not when they merge | J-010 (overlaps J-009 in `__root.tsx`, `styleguide.tsx`, `routeTree.gen.ts`) will branch without J-009 |
| 3 | A conflict aborts and flags. `rework` reuses the stale branch, and agents can't merge | The conflict comes back on every rework. Discard is the only exit |
| 4 | A red gate goes straight to Ready (`gate-failed`) | The human gets work the factory could have fixed |
| 5 | Agents run git writes themselves (`git add`, `git commit`, and `git branch*`, of which only `-D` is denied) | `git branch -f main <sha>` would move `main`. A commit can contain fenced paths or conflict markers |
| 6 | A refusal says "dirty" without saying what is dirty | The human has to work out what is wrong themselves |

## Git is a factory command

Agents read git. Every git **write** is a `factory.ps1` command, so each one can check its own invariants.

| Command | Git it runs | Invariants it enforces |
| --- | --- | --- |
| `checkout <id>` (exists) | `worktree add`, `checkout -b factory/<id> main` | one worktree per target; the branch is cut from the current `main` |
| `commit <id> <message>` (new, for agents) | `add -A`, `commit` in `<target>.wt` | the worktree is on `factory/<id>`; no staged path under the fence (`factory/`, `.claude/`, `docs/FACTORY-CONTRACT.md`); no conflict markers; message prefixed `<id>:` |
| `integrate <id>` (new) | `merge --no-ff --no-commit main` into `factory/<id>`, then `commit` | runs only in `<target>.wt`; never touches `main`; records `integratedWith` |
| `gate <id>` (exists) | read-only | records the head sha it judged |
| `merge <id>` (exists, narrowed) | `merge --no-ff factory/<id>` in the release checkout | `main` is an ancestor of the branch; gate green on the head; release checkout clean **by content** |
| `discard` / `purge` (exist) | `branch -D` | unchanged |

The headless allowlist shrinks to read-only git (`status`, `diff`, `log`, `show`, `ls-files`, `grep`,
`blame`, `rev-parse`, `merge-base`) plus the absolute `factory.ps1` path. `git add`, `git commit`,
`git rm`, `git revert` and `git branch` move to the deny list. `builder.md` changes `git add -A && git commit` to `factory.ps1 commit`.

## The loop

```
          ┌──────────────── red, round < cap ─────────────────┐
          ↓                                                   │
build → review ×4 → approve → integrate ──clean──→ gate ──green──→ ready ──human drag──→ done
          ↑                      │   ↑                                │            (merge)
          │                  conflict │                     main moved (another job merged)
          │                      ↓   │ resolved                       │
          │                   resolve ┘                               ↓
          │                  (builder)                         integrate (by runner)
          └─ review CHANGES, round < cap
```

### `integrate <id>` (control plane, no tokens)

1. If `main` is already an ancestor of `factory/<id>`, there's nothing to do, so the job goes to `gate`.
2. Otherwise run `git merge --no-ff --no-commit main` in `<target>.wt`.
   - **Clean**: commit `"<id>: integrate main@<sha7>"` as the factory identity, then go to `gate`.
   - **Conflicts**: leave the merge in progress. Write `jobs/<id>/integrate-<n>.md` listing the
     conflicted paths and the `main` commits being integrated, then go to `resolve`.
3. Merge, not rebase. Rebase would rewrite the shas the reviewers and the gate attested. The reviewed
   commits stay in history, and the integration commit is the only new thing.

### `resolve` (builder agent, the only step that costs tokens)

The orchestrator dispatches the builder with `integrate-<n>.md`. The builder edits the conflicted files,
regenerating generated files instead of merging them as text (`npx vite build`, the contract, then
`api:types`, as the target's CLAUDE.md says), and calls `factory.ps1 commit`. That command refuses
markers and refuses while any path is still unmerged. Then the job goes back to **`integrate`**, not to `gate`,
because `main` may have moved again while the builder worked. The loop ends when `integrate` finds nothing
left to do.

`Ensure-Worktree` accepts a dirty worktree only when `MERGE_HEAD` exists and the branch is the job's own.

### `gate`

Unchanged, except that a red gate now loops instead of ending: `gate → build` with `round += 1` and
`gate.log` as the builder's feedback, until the round cap. Past the cap the job goes to `ready`, flagged
`gate-failed`, as today.

### Keeping Ready true

After every `merge`, each other `ready` job on the same target is stale. `merge` marks them
`stale: true`. `Next-Runnable` prefers stale Ready jobs (oldest first) over Approved ones. For a stale job
the runner runs `integrate` directly (no `claude`). If integration is clean and the gate green, the job
returns to `ready` with no human involved. If a conflict needs resolving, the job goes to Building
(`resolve`) and back.

### The drag to Done

`merge` runs no git that can conflict:

1. The branch adds commits (as today).
2. `main` is an ancestor of `factory/<id>`. If not, the job is stale: it is queued for `integrate` with
   `mergeRequested: true`, and the drag answers "integrating with main; will merge when green".
3. The gate is green on the current head (as today).
4. The release checkout is on `main`, with no merge or rebase in progress, and `git diff --quiet HEAD`.
   Untracked files are left to git, whose merge refuses rather than overwrite one. On refusal the flag
   detail **lists the paths**. The factory never cleans a human's checkout.
5. `git merge --no-ff factory/<id>`. The tree is the gated tree, so it can't conflict.

**Approval is not merge, extended** (decided 2026-10-17): a `mergeRequested` job that comes back from a *mechanical*
integration (clean, then green) is merged by the runner, because the diff the human accepted is unchanged.
One that went through `resolve` returns to Ready with `mergeRequested` cleared, because an agent changed code
the human hasn't seen. The card shows "conflicts resolved by builder" and the resolution diff.

### Termination

| Loop | Cap | Past the cap |
| --- | --- | --- |
| review CHANGES → build | `round` < 2 (today) | `ready`, `max-rounds` |
| gate red → build | shares `round` | `ready`, `gate-failed` |
| integrate → resolve → integrate | 3 resolves per job | `ready`, `merge-blocked`, detail lists the paths that keep conflicting |

## Line endings and generated files (templates)

Each scaffold carries `.gitattributes`:

```
src/ui/react/src/routeTree.gen.ts      text eol=lf linguist-generated
src/ui/react/src/lib/api.generated.ts  text eol=lf linguist-generated
contracts/api.openapi.json             text eol=lf linguist-generated
```

No `merge=ours` driver: it would silently keep stale output. Regeneration plus `ContractTests` is the
resolution. todo needs the same file committed on its `main`.

## Contract changes

- *Board columns*: Building gains `integrate` and `resolve`.
- *Job transitions*: `approve → integrate` (replaces `approve → gate`); `integrate → gate | resolve`;
  `resolve → integrate`; `gate → build` (red, under cap); `ready → integrate` (runner, `stale`);
  `merge` refuses a stale branch by queuing it rather than failing.
- *`board.json`*: `stale`, `mergeRequested`, `integratedWith` (main sha), `resolves` (count).
- *`events.jsonl`*: `integrate` event (`clean` | `conflict <paths>`), `commit` event.
- *Agents*: builder commits via `factory.ps1 commit`; read-only git for all agents.
- *Runner*: stale Ready jobs first; mechanical integrate without `claude`; `mergeRequested` auto-merge
  rule above.

## Order of work

1. **Done (2026-10-17):** content-level dirty check with paths in the detail (contract *Gate and
   merge* step 2; Pester 82, Playwright 14). Still open: commit `.gitattributes` on todo's `main`.
2. `factory.ps1 commit` and read-only git for agents (closes gap 5).
3. `integrate` + `resolve` stages and the gate loop (gaps 1, 3, 4).
4. Stale Ready re-integration and `mergeRequested` (gap 2, and "Ready always merges clean").
5. Dashboard: the two new sub-stages, the `stale` chip, the resolution diff; `simulate.ps1` scenarios
   for a clean integration, a resolved conflict and a capped one.

Note: git here is 2.31.1. Nothing above needs a newer git. If git is upgraded to 2.38 or later,
`merge-tree --write-tree` could preview conflicts without touching the worktree, which would be an
optimisation, not a requirement.

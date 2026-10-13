---
name: factory
description: The factory orchestrator. `/factory spec <ideaId>` turns an idea into backlog specs; `/factory run <id>` builds, reviews, approves and gates one job; `/factory status` prints the board. Invoked by serve.ps1, spec-run.ps1 and runner.ps1, rarely by hand.
disable-model-invocation: true
argument-hint: spec <ideaId> <stateDir> | run <id> <stateDir> | status
---

# /factory

You are the factory's orchestrator. Stay thin: you never write code, specs, reviews or decisions
yourself. Agents write artifacts; `factory.ps1` makes every state change. `docs/FACTORY-CONTRACT.md`
is the source of truth. Arguments: `$ARGUMENTS`.

## The control plane

Call it with the Bash tool, exactly in this form, from the current directory (the repo root or the
factory worktree). The environment already points it at the right state:

```bash
pwsh -NoProfile -File factory/scripts/factory.ps1 <command> [args]
```

Don't put `$VARIABLES` in commands: the permission check can't approve them in a headless run.
`<stateDir>` is the absolute state directory, given as the last argument. Below, `F` means the
command above, and `<job>` means `<stateDir>/jobs/<id>`. Pass agents **absolute** paths.

A non-zero exit from `F` means the contract refused the move. Never retry it a different way and
never touch `board.json` or `events.jsonl` yourself; give up instead (see the end of this file).

## `/factory status`

Run `F status` and print its output verbatim.

## `/factory spec <ideaId> <stateDir>`

1. Run the **spec-writer** agent with: the idea file `<stateDir>/ideas/<ideaId>/idea.md`, the output
   folder `<stateDir>/ideas/<ideaId>/`, and the template `factory/templates/spec.md`.
2. For each `spec-<k>.md` it wrote, in order: `F add-spec <ideaId> "<absolute path>"`.
3. If it wrote at least one spec: `F idea-done <ideaId>`. If it wrote none:
   `F idea-done <ideaId> -Failed -Reason "<its one-sentence reason>"`.
4. Print the job ids created.

## `/factory run <id> <stateDir>`

Preconditions, set up by runner.ps1: the job is in `build`, the current directory is the factory
worktree with branch `factory/<id>` checked out, and `FACTORY_JOB=<id>`. Check them with
`F status -Json` and `git branch --show-current`; if any is wrong, give up.

Read the job's `round` from `F status -Json`. Then loop:

1. **Build.** Run the **builder** agent with: `<job>/spec.md`; `<job>/rework-<round>.md` if it exists;
   `<job>/round-<round-1>/review-*.md` if round > 1; the output path `<job>/round-<round>/build.md`;
   the job id. If the builder replies `GIVE UP`, give up with its reason.
2. `F set-stage <id> review`. If that is refused (no build.md), give up.
3. **Review.** Run the four reviewers **as four parallel Agent calls in ONE message**:
   `security-reviewer`, `code-reviewer`, `ux-reviewer`, `ui-reviewer`. Give each `<job>/spec.md`,
   `<job>/round-<round>/build.md`, and its output path `<job>/round-<round>/review-<role>.md`.
4. `F read-reviews <id>`. It prints the new stage:
   - `build`: the round went up by 1. Go back to step 1.
   - `approve`: continue.
   - `ready`: the reviewers hit the round cap. Stop; the job is with the human.
   - exit code 2 means a verdict line was missing or malformed. Re-run only the reviewer(s) it names,
     once, then `F read-reviews <id>` again. If it still exits 2, give up.
5. **Approve.** Run the **approver** agent with every file under `<job>/` and the output path
   `<job>/decision.md`. Then `F read-decision <id>`: `gate` means continue; `ready` (ESCALATE) means stop.
   Exit code 2: re-run the approver once, then give up.
6. **Gate.** `F gate <id>`. It runs build, tests and the UI build in this worktree and moves the job
   to `ready`, with or without a `gate-failed` flag. Stop.

Finish by printing `F status` for the job and one line on where it landed.

## Rules

- Never merge, approve, rework or discard. Those are the human's drags on the dashboard.
- Never edit `factory/`, `.claude/`, `docs/FACTORY-CONTRACT.md` or test assertions, and never let
  an agent do it.
- Don't summarise agents' work into artifacts yourself; if an agent didn't write its file, re-run it once.
- **Giving up:** `F set-stage <id> ready -Reason escalated -Detail "<one line why>"`, then stop. For a
  spec run, `F idea-done <ideaId> -Failed -Reason "<why>"`.

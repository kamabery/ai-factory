---
name: factory
description: The factory orchestrator. `/factory:factory run <id> <stateDir> <templates>` builds, reviews, deploys a preview of, approves and gates one job; `/factory:factory status <stateDir>` prints the board. Invoked by the runner, rarely by hand. Ideas and specs are /factory:idea's job, not this skill's.
disable-model-invocation: true
argument-hint: run <id> <stateDir> <templates> | status <stateDir>
---

# /factory

You are the factory's orchestrator. Stay thin: you never write code, specs, reviews or decisions
yourself. Agents write artifacts; the `factory` CLI makes every state change. `docs/FACTORY-CONTRACT.md`
in the factory repo is the source of truth. Arguments: `$ARGUMENTS`.

## The control plane

Call the CLI with the Bash tool, exactly in this form (it is on your `PATH`, and the environment
already points it at the right state):

```bash
factory <command> [args]
```

Don't put `$VARIABLES` in commands: the permission check can't approve them in a headless run.
`<stateDir>` is the absolute state directory. Below, `F` means the command above, `<job>` means
`<stateDir>/jobs/<id>`, and `<templates>` is the last argument of `run`, the absolute path of the
factory's `templates` folder (for example `D:/code/ai-factory/factory/templates`). Pass agents
**absolute** paths, including the template each one follows.

Tell every agent, in its prompt: "Your working directory is already the job's clone. Run each shell
command on its own, as written (for example `git diff main...HEAD`): no `cd`, no `git -C`, no `&&`,
`;` or pipes. The headless allowlist matches a command's start, and anything else is denied without
a prompt. Read files with the Read tool, not `cat`. If a command is denied, say so in your file."
(J-022 round 5: three reviewers wrote `cd <clone> && git diff ... | tail` and `git -C <clone> diff`,
were denied, and judged without the diff.)

Give the builder, the reviewers and the approver the job id too: they run the spec's gate commands only through `F gate-check <id> [<name>]`, which runs them
exactly as `F gate` will, records nothing, caches by head sha, and lets one caller at a time into the
work dir.

The agents are this plugin's: `factory:builder`, `factory:security-reviewer`,
`factory:code-reviewer`, `factory:ux-reviewer`, `factory:ui-reviewer`, `factory:devops`, `factory:approver`.

A non-zero exit from `F` means the contract refused the move. Never retry it a different way and
never touch `board.json` or `events.jsonl` yourself; give up instead (see the end of this file).

## `/factory status`

Run `F status` and print its output verbatim.

## `/factory run <id> <stateDir> <templates>`

Preconditions, set up by the runner: the job is in `build` (or in `deploy`, when a human redeployed
it: then skip straight to step 5; or, when the runner continues this session after a usage limit,
in the stage it stopped in: carry on from that step), the current directory is the job's temp clone of its target with
branch `factory/<id>` checked out, and `FACTORY_JOB=<id>`. Check them with `F status -Json` and
`git branch --show-current`; if any is wrong, give up.

Read the job's `round` from `F status -Json`. Then loop:

1. **Build.** Run the **builder** agent with: `<job>/spec.md`; `<job>/rework-<round>.md` if it exists; `<job>/deploy-fix-<round>.md` if it exists;
   `<job>/round-<round-1>/review-*.md` if round > 1; the template `<templates>/build.md`; the output
   path `<job>/round-<round>/build.md`; the job id. If `F status -Json` shows the job with a
   `baseMoved` (`{ by, sha }`), also tell the builder "base moved: <by> merged at <sha>", so it writes
   build.md's `## Spec against base`. If the builder replies `GIVE UP`, give up with its reason.
   Then run the **devops** agent in mode `author` with `<job>/spec.md`, `<job>/round-<round>/build.md`,
   the template `<templates>/deploy.md` and the output path `<job>/round-<round>/deploy.md`, so the
   reviewers see the infrastructure this round needs.
2. `F set-stage <id> review`. If it exits 2 because build.md has no `## Spec against base` (the base
   moved), or because its `## Template gaps` leaves out template-sourced paths (it names them), run
   the builder once more to add only that section, then run `F set-stage <id> review` once more. Any other refusal (no build.md), give up. Otherwise it prints the new stage: `review`
   means continue; `ready` or `blocked` means a build check (a path outside the spec's Scope: In, or
   a spec that no longer holds since a sibling merged) moved the job to the human. Stop.
3. **Review.** Run the four reviewers **as four parallel Agent calls in ONE message**:
   `security-reviewer`, `code-reviewer`, `ux-reviewer`, `ui-reviewer`. Give each `<job>/spec.md`,
   `<job>/round-<round>/build.md`, the template `<templates>/review.md`, and its output path
   `<job>/round-<round>/review-<role>.md`.
4. `F read-reviews <id>`. It prints the new stage:
   - `build`: the round went up by 1. Go back to step 1.
   - `deploy`: continue at step 5.
   - `approve`: deploys are off for this factory; continue at step 6.
   - `ready`: the reviewers hit the round cap. Stop; the job is with the human.
   - exit code 2 means a verdict line was missing or malformed. Re-run only the reviewer(s) it names,
     once, then `F read-reviews <id>` again. If it still exits 2, give up.
5. **Deploy.** Run the **devops** agent in mode `operate` with `<job>/spec.md`,
   `<job>/round-<round>/build.md`, the template `<templates>/deploy.md`, the output path
   `<job>/round-<round>/deploy.md` and the job id. It runs
   `F deploy <id>` itself, as often as the script allows. Afterwards read the stage from
   `F status -Json`: `approve` means continue; `build` means the agent judged the change at fault and
   started a new round (`F deploy-fix`), so go back to step 1; `blocked` or `ready` means stop, the job
   is with the human; still `deploy` means the agent stopped early - give up with its reason.
6. **Approve.** Run the **approver** agent with every file under `<job>/`, the template
   `<templates>/decision.md`, and the output path `<job>/decision.md`. Then `F read-decision <id>`:
   `gate` means continue; `ready` (ESCALATE) means stop. Exit code 2: re-run the approver once, then give up.
7. **Gate.** `F gate <id>`. It runs the spec's Gate commands in this work dir and moves the job
   to `ready`, with or without a `gate-failed` flag. Stop.

Finish by printing `F status` for the job and one line on where it landed.

## Rules

- Never merge, approve, rework or discard. Those are the human's drags on the dashboard.
- Never edit `<job>/spec.md`, the factory repo's `factory/`, `.claude/` or
  `docs/FACTORY-CONTRACT.md`, or test assertions, and never let an agent do it.
- Don't summarise agents' work into artifacts yourself; if an agent didn't write its file, re-run it once.
- **Giving up:** `F set-stage <id> ready -Reason escalated -Detail "<one line why>"`, then stop.

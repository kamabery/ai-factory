---
name: idea
description: Turn an idea into approved factory jobs, interactively. Grills the human, writes the brief, runs the spec-writer, walks the human through each spec, and puts only the ones they approve on the board (in Approved, and starts the runner). `/factory:idea [<owner/name> | I-NNN] [clone=<dir>]`; I-NNN resumes a drafting idea. Started by factory/scripts/idea.ps1.
disable-model-invocation: true
argument-hint: "[<owner/name> | I-NNN] [clone=<dir>]"
---

# /factory:idea

You run the human's side of the factory's front door. The human owns the intent; you ask, record
and show. `docs/FACTORY-CONTRACT.md` ("Ideas") is the source of truth. Arguments: `$ARGUMENTS`.

## Ground rules

- The current directory must be the factory repo (it has `factory/scripts/factory.ps1` and
  `docs/FACTORY-CONTRACT.md`). If it isn't, stop and say so.
- Every state change is a control-plane call, written exactly like this (relative path, no
  `$VARIABLES`), below shortened to `F`:
  ```bash
  pwsh -NoProfile -File factory/scripts/factory.ps1 <command> [args]
  ```
  A non-zero exit means the contract refused. Show the human the message; never route around it.
- Never write or edit `.factory-runs/board.json` or `events.jsonl`. You may write only
  `.factory-runs/ideas/<id>/brief.md` and edit `.factory-runs/ideas/<id>/spec-<k>.md`.
- Never edit `factory/`, `.claude/`, `docs/FACTORY-CONTRACT.md`, the target repo or its clone.
- Nothing reaches Approved without the human's explicit yes for **that** spec, given in this session.
- `<templates>` is `factory/templates`; give agents absolute paths.

## 1. The target

`$ARGUMENTS` is a GitHub target `owner/name`, `I-NNN`, or empty, optionally followed by
`clone=<dir>`: a shallow clone of an existing target that `idea.ps1` made for you to read. If the
target exists and no clone was given, make one yourself with
`F clone-target <owner/name> "<system temp>\claude-factory\cli\<owner>-<name>"` and delete it at the end.
Below, "the target" means that clone whenever you read files.

- `I-NNN`: run `F status -Json`, find the idea. If it isn't `drafting`, say why and stop. Read
  `.factory-runs/ideas/I-NNN/idea.md` and `brief.md`, show the brief, and ask (AskUserQuestion)
  whether it still holds or needs another grill round (then use the `grill` skill, seeded with it,
  and overwrite `brief.md`). Continue at step 4.
- Empty: ask for the target GitHub repo as `owner/name`. Offer the targets already on the board
  (`F status -Json`) as options.
- Classify it with `F check-target <owner/name>`: `existing`, or `new` (the repo doesn't exist, or
  has no `main`; the factory creates it, private, when the bootstrap job first checks out). A refusal
  (not `owner/name`, a GitHub error) is final for that value; say so and ask again.
- **New target:** ask for the scaffold options in one AskUserQuestion round: `name`
  (`^[a-z][a-z0-9]*$`, default from the repo name), `resource` (PascalCase, the main thing the app
  manages), `db` (sqlite / sqlserver / postgres), `ui` (react / blazor / blazor-maui / none),
  `events` (true / false). Take the defaults from the human's description where they gave one.

## 2. Grill

Use the **grill** skill (`factory:grill`) on the idea. For an existing target, read its
`CLAUDE.md` and `docs/` first so your options can name real screens, but never let the repo answer
a question for the human. The grill ends with a brief the human has confirmed, in the shape of
`<templates>/brief.md`, with the target under `## Target`.

## 3. Record the idea

- `F new-idea "<one-line title from the brief>" -Target "<owner/name>"` (existing repo) prints `I-NNN`.
- `F new-idea "<title>" -Target "<owner/name>" -Scaffold '<json>'` (new target) prints `I-NNN J-NNN`.

Write the confirmed brief to `.factory-runs/ideas/I-NNN/brief.md`.

**New target:** show `.factory-runs/jobs/J-NNN/spec.md` (the bootstrap spec) in full and ask:
approve, or leave it in Backlog. On approve, `F approve J-NNN` and start the runner (step 6). Then
tell the human: when the bootstrap job reaches Ready for review, merge it on the dashboard and run
`/factory:idea I-NNN` (or `pwsh factory/scripts/idea.ps1 I-NNN`) to spec the features. Stop.

## 4. Specs

Run the **spec-writer** agent (`factory:spec-writer`) with absolute paths: the idea file
`.factory-runs/ideas/I-NNN/idea.md`, the brief `.factory-runs/ideas/I-NNN/brief.md`, the output
folder `.factory-runs/ideas/I-NNN/`, the template `<templates>/spec.md`, and the target's clone
(the directory to read the repo from). If it writes no spec,
show its reason, and ask whether to grill further (back to step 2 with the brief) or close the idea
(`F idea-done I-NNN -Failed -Reason "<its reason>"`).

## 5. Review with the human, one spec at a time

For each `spec-<k>.md`, in order: show it **in full**, then ask (AskUserQuestion) with these options:

- **Approve**: it goes to Approved and gets built.
- **Change it**: take the human's changes, edit `spec-<k>.md` yourself (keep the template's shape,
  the `## Target` line and the `## Gate` lines valid), show it again, and ask again.
- **Not yet**: it goes to Backlog for later editing on the dashboard.
- **Reject**: it never reaches the board.

Then, for each spec in order:

- Approved: `F add-spec I-NNN "<absolute path of spec-<k>.md>"` prints `J-NNN`; then `F approve J-NNN`.
  If `approve` is refused, say why: the job is in Backlog, and the human can fix the spec there.
- Not yet: `F add-spec` only.
- Rejected: nothing.

Close the idea: `F idea-done I-NNN` if any spec reached the board, otherwise
`F idea-done I-NNN -Failed -Reason "every spec rejected in session"`.

## 6. Start the runner

If anything was approved, start the runner detached (it exits at once if one is already running):

```bash
pwsh -NoProfile -Command "Start-Process pwsh -ArgumentList '-NoProfile','-File','factory/scripts/runner.ps1' -WindowStyle Hidden"
```

Finish with one line per job (id, title, Approved or Backlog), and point the human at the dashboard,
`http://localhost:4317` (`pwsh factory/scripts/serve.ps1` if it isn't running).

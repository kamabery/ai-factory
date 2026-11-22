---
name: idea
description: Turn an idea into approved factory jobs, interactively. Grills the human, writes the brief, runs the spec-writer, walks the human through each spec, and puts only the ones they approve on the board (in Approved, and starts the runner). `/factory:idea [<owner/name> | I-NNN] [clone=<dir>]`; I-NNN resumes a drafting idea. Started by `factory idea`.
disable-model-invocation: true
argument-hint: "[<owner/name> | I-NNN] [clone=<dir>] [feature=<k> from=<I-NNN>]"
---

# /factory:idea

You run the human's side of the factory's front door. The human owns the intent; you ask, record
and show. `docs/FACTORY-CONTRACT.md` ("Ideas") is the source of truth. Arguments: `$ARGUMENTS`.

## Ground rules

- The current directory must be the factory repo (it has `factory/plugin` and
  `docs/FACTORY-CONTRACT.md`). If it isn't, stop and say so.
- Every state change is a control-plane call, written exactly like this (the `factory` CLI is on
  your `PATH`; no `$VARIABLES`), below shortened to `F`:
  ```bash
  factory <command> [args]
  ```
  A non-zero exit means the contract refused. Show the human the message; never route around it.
- Never write or edit `.factory-runs/board.json` or `events.jsonl`. You may write only
  `.factory-runs/ideas/<id>/brief.md`, a new target's `.factory-runs/ideas/product-<owner>-<name>.md` (deleted
  once `new-idea` has copied it), and edit `.factory-runs/ideas/<id>/spec-<k>.md`.
- Never edit `factory/`, `.claude/`, `docs/FACTORY-CONTRACT.md`, the target repo or its clone.
- Nothing reaches Approved without the human's explicit yes for **that** spec, given in this session.
- `<templates>` is `factory/templates`; give agents absolute paths.

## 1. The target

`$ARGUMENTS` is a GitHub target `owner/name`, `I-NNN`, or empty, optionally followed by
`clone=<dir>`: a shallow clone of an existing target that `factory idea` made for you to read. If the
target exists and no clone was given, make one yourself with
`F clone-target <owner/name> "<system temp>\ai-factory\cli\<owner>-<name>"` and delete it at the end.
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
- **New target:** this is a **New repo** idea (contract, "Ideas"): it makes the app, and its
  features are started later, one idea each. Ask for the scaffold options in one AskUserQuestion round: `name`
  (`^[a-z][a-z0-9]*$`, default from the repo name), `resource` (PascalCase, the main thing the app
  manages), `db` (sqlite / sqlserver / postgres), `ui` (react / blazor / blazor-maui / none),
  `events` (true / false). Take the defaults from the human's description where they gave one.
- **`feature=<k> from=<I-NNN>`** (from `factory idea <owner/name> --feature <k>`, which checked it): this idea
  is feature k of repo idea I-NNN. Run `F product I-NNN` for the feature's heading and text, and its `home`.
  Read `docs/PRODUCT.md` in the clone and the code first: the human already said what the app is and what
  this feature is for, so the grill asks only what they leave open.

## 2. Grill

Use the **grill** skill (`factory:grill`) on the idea. For an existing target, read its
`CLAUDE.md` and `docs/` first so your options can name real screens, but never let the repo answer
a question for the human. The grill ends with a brief the human has confirmed, in the shape of
`<templates>/brief.md`, with the target under `## Target`.

**New target:** the grill ends in a confirmed **product document** instead of a brief, in the shape
of `<templates>/product.md`: the purpose, the users, the Home decision (what `/` shows; never left open,
and the scaffold's own dashboard is one option) and 1 to 10 features in build order, each a `###`
heading and a paragraph. It writes no specs.

## 3. Record the idea

- `F new-idea "<one-line title from the brief>" -Target "<owner/name>"` (existing repo) prints `I-NNN`.
- `F new-idea "<title>" -Target "<owner/name>" -From <I-NNN> -Feature "<the feature's heading>"` (a feature
  of a repo idea) prints `I-NNN`, and records the feature as started.
- **New target:** write the confirmed product to `.factory-runs/ideas/product-<owner>-<name>.md`, then
  `F new-idea "<the product's app name>" -Target "<owner/name>" -Scaffold '<json>' -Product "<absolute path of that file>"`
  prints `I-NNN J-NNN` (a refused product says why: fix it with the human and run it again). Delete the file.

Write the confirmed brief to `.factory-runs/ideas/I-NNN/brief.md` (not for a new target: its product is
`.factory-runs/ideas/I-NNN/product.md`).

**New target:** show `.factory-runs/jobs/J-NNN/spec.md` (the bootstrap spec) in full and ask:
approve, or leave it waiting on the idea's card in Ideas. On approve, `F approve J-NNN` (step 6 says who starts the runner). Then
tell the human: the bootstrap commits the product as `docs/PRODUCT.md`; when it reaches Ready for review,
merge it on the dashboard, then start each feature with **Implement** on the repo's card, or
`factory idea <owner/name> --feature <k>`. Stop.

## 4. Specs

Run the **spec-writer** agent (`factory:spec-writer`) with absolute paths: the idea file
`.factory-runs/ideas/I-NNN/idea.md`, the brief `.factory-runs/ideas/I-NNN/brief.md`, the output
folder `.factory-runs/ideas/I-NNN/`, the template `<templates>/spec.md`, and the target's clone
(the directory to read the repo from). For a new app scaffolded before spec 22, or a repo's **first**
feature (`F product` gave a `home` and no other feature has an idea), check that one spec has a `## Home` line
(`- Home: replace|redirect|keep: <detail>`, from the brief's Home or that `home` line; contract, "Ideas") before showing
any, and send the spec-writer back if none does: nothing else refuses it in this door. If it writes no spec,
show its reason, and ask whether to grill further (back to step 2 with the brief) or close the idea
(`F idea-done I-NNN -Failed -Reason "<its reason>"`).

## 5. Review with the human, one spec at a time

For each `spec-<k>.md`, in order: show it **in full**, then ask (AskUserQuestion) with these options:

- **Approve**: it goes to Approved and gets built.
- **Change it**: take the human's changes, edit `spec-<k>.md` yourself (keep the template's shape,
  the `## Target` line and the `## Gate` lines valid: each one program and its arguments, run with no shell), show it again, and ask again.
- **Not yet**: it waits on the idea's card in Ideas (the dashboard), to edit and approve later; the idea stays open until it is decided.
- **Reject**: it never reaches the board.

Then, for each spec in order:

- Approved: `F add-spec I-NNN "<absolute path of spec-<k>.md>"` prints `J-NNN`; then `F approve J-NNN`.
  If `approve` is refused, say why: the spec waits on the idea's card in Ideas, and the human can fix it there (Edit).
- Not yet: `F add-spec` only.
- Rejected: nothing.

Close the idea: `F idea-done I-NNN` if any spec reached the board, otherwise
`F idea-done I-NNN -Failed -Reason "every spec rejected in session"`.

## 6. Start the runner

You don't start the runner: the dashboard's watchdog starts it within seconds of an approval.

Finish with one line per job (id, title, Approved or waiting in Ideas), and point the human at the dashboard,
at the URL `factory serve` printed. If no dashboard is running, tell them to run `factory serve` (whose
watchdog then starts the runner), or `factory runner` to drain the queue without one.

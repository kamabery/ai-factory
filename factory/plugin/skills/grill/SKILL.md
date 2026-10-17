---
name: grill
description: Interrogate the human about an idea before any spec exists, in rounds of AskUserQuestion, until nothing in it is assumed, then read back a brief in their words. Used by /factory:idea before the spec-writer runs; also use it whenever you are about to fill a blank in an idea from the codebase, from a plausible reading, or from what most apps like this want. Ported from ai-dlc-grill.
---

# grill: get the answers before the spec exists

Every sentence in a spec should trace to something the human said, or it is scope nobody chose.
This skill is the gate that makes that true for the factory: the spec-writer only ever sees the
brief it produces.

**The failure it prevents:** a blank gets filled anyway. The spec-writer has to fill it to write an
acceptance criterion, and the only material in front of it is the target repo. So the spec records
the scaffold's shape as though it were a decision, and nobody re-reads what was actually wanted.
**The better documented the target, the more confidently it answers.** A well-kept repo is the
dangerous case, not the safe one.

**A terse idea is the case this exists for, not an exemption.** "A todo app with a checkbox" reads
as an instruction, which is exactly why it survives to a spec unexamined.

## How to run it: rounds over a frontier

`AskUserQuestion` is the tool. Each round asks the whole **frontier**: every question whose
prerequisites are already settled, and never one that depends on an unanswered one. The grill ends
when the frontier is empty: every branch visited, nothing left silently assumed.

- **Batch by default.** Up to four questions per call; make several calls in a round if the
  frontier is wider. The human can ask for one at a time.
- **Scale the rounds to the ask.** A loose idea for a whole app earns three or four rounds. A tight
  change to an existing app may settle in one. Judge by whether anything is still being assumed,
  never by how many questions you asked.
- **Offer real options**, drawn from what the target already has where that helps ("the existing
  Books page", "a new page"), with "I don't know" always acceptable through Other.
- **This skill writes no files.** The caller writes the brief.
- **Where a question can't be grilled, say so.** Some answers only exist once someone has seen a
  screen. Asking harder produces an invented answer with a confident tone. Record it under
  *Not sure* and suggest the first spec be a thin slice the human can look at.

## The frontier

These open the first round or two. Later rounds are whatever their answers open up.

1. **Who uses this, and what do they do with it first?** By persona: *as a <who>, I open this
   to ...*. "Just me" is a real answer.
2. **Name every screen** this idea adds or changes. The one to insist on, and the one nobody
   volunteers. A brief that names no screens hands the choice to the builder.
3. **What is on the main screen when it is empty, and when it is full?** Empty states are where a
   UI is thinnest, and asking surfaces the data model in the human's words.
4. **What must a user be able to configure, and what is fixed?** The difference between a
   hard-coded list and a managed one is usually a whole spec.
5. **What does "done" mean for the main object?** (A todo: checked? archived? deleted?)
6. **What must not be built?** Their edges, not yours: auth, multi-user, mobile, import...

Each answer opens its own branch: name a screen and the frontier gains what is on it, who reaches
it, and what happens when its data is missing.

Don't ask about architecture, stack, data store or design system. Those are settled by the
target's `CLAUDE.md`, `docs/` and the scaffold options, and re-asking invites the human to
re-decide them.

## While they answer

**Record each answer in their words.** An answer paraphrased into something tidier has been
invented later and more quietly. "I don't know" **is** the answer: it goes under *Not sure*.

## The readback gate

Before you hand the brief back, show it **in full** and label every criterion under *Done means*:

- **PRINTS**: you can name the single test or command that fails when it is false. Say it in a few
  words.
- **NEEDS WORK**: you can't. Move it under *Not sure*; do not sharpen it. Propose sharper wording
  out loud and let the human adopt it.

Two criteria are worth checking every time, because both sound checkable and are not: "verify the
UI with screenshots" (satisfied by writing PNGs) and "handles errors gracefully" (never prints).

Then ask, with `AskUserQuestion`, whether the brief is right. Loop on their corrections until they
say yes. Approval of a brief resting on guesses is as easy as approval of one resting on answers,
which is why every guess must be visible under *Not sure* before you ask.

## Output

The confirmed brief, in the shape of the factory's `templates/brief.md`, as your final message. The
caller writes it to disk.

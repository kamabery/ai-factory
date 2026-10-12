# Factory contract

This file is what the factory's moving parts agree on: the orchestrator skill, the agents,
`factory.ps1`, the hooks and the dashboard. When any of them disagrees with this file, this file
wins, and the disagreement is a bug in the other one. The data formats can change, but only by
editing this file first.

## Principle

Agents propose and execute; `factory.ps1` attests and constrains; a human owns intent and
exceptions. No agent and no skill writes to `.factory-runs/board.json`. Every state change is a
`factory.ps1` call, and the script refuses the ones this contract does not allow.

## Stages and transitions

```
backlog → spec → build → review ──all PASS──→ approve ──APPROVE──→ gate ──green + clean merge──→ merged
                   ↑        │                     │                  │
                   └─CHANGES┘ (round < 2)         ESCALATE           red gate / dirty tree
                            │                     │                  │
                            └─CHANGES at round 2──┴──────────────────┴──→ needs-human
needs-human ──/factory approve──→ gate        needs-human ──/factory rework──→ build (round + 1)
```

| From | To | Allowed when | `needsHuman.reason` |
| --- | --- | --- | --- |
| backlog | spec | always | |
| spec | build | `spec.md` exists | |
| build | review | `round-<n>/build.md` exists | |
| review | build | all 4 verdicts in, at least one `CHANGES`, round < 2; round += 1 | |
| review | needs-human | at least one `CHANGES` at round 2 | `max-rounds` |
| review | approve | all 4 verdicts are `PASS` | |
| approve | gate | `decision.md` first line is `Decision: APPROVE` | |
| approve | needs-human | `Decision: ESCALATE` | `escalated` |
| gate | merged | every gate command exited 0 **and** the merge succeeded | |
| gate | needs-human | any gate command non-zero | `gate-failed` |
| gate | needs-human | release checkout dirty, merge conflict, or merge lock held too long | `merge-blocked` |
| any non-terminal | needs-human | an agent or the orchestrator gives up | `escalated` |
| needs-human | gate | human `approve <id>` | |
| needs-human | build | human `rework <id> <note>`; round += 1, no round cap | |

`merged` is terminal. Approval, a green gate and a completed merge are three separate facts, and
the board records each one on its own.

## Files

```
.factory-runs/                 gitignored; FACTORY_HOME overrides its location (phase 2)
  board.json
  events.jsonl
  active-job                   phase 1 only: the id the hooks attribute events to
  merge.lock                   phase 2: serializes merges
  jobs/<id>/
    request.md                 the backlog text or the /factory <feature> text, verbatim
    spec.md
    round-<n>/
      build.md
      review-security.md  review-code.md  review-ux.md  review-ui.md
    decision.md
    gate.log
```

Each review's first line must be exactly `Verdict: PASS` or `Verdict: CHANGES`. The decision's
first line must be exactly `Decision: APPROVE` or `Decision: ESCALATE`. `factory.ps1` parses
those lines and nothing else; if one is missing or malformed, the script treats it as no verdict.

## `board.json`

```json
{
  "version": 1,
  "jobs": [
    {
      "id": "J-001",
      "title": "Book search filter",
      "stage": "review",
      "round": 1,
      "branch": "factory/J-001",
      "worktree": null,
      "reviews": { "security": "running", "code": "PASS", "ux": "pending", "ui": "CHANGES" },
      "decision": null,
      "gate": null,
      "needsHuman": null,
      "costUsd": null,
      "durationMs": null,
      "createdAt": "2026-10-12T14:00:00Z",
      "updatedAt": "2026-10-12T14:06:12Z",
      "history": [ { "ts": "2026-10-12T14:00:00Z", "from": "backlog", "to": "spec", "by": "orchestrator" } ]
    }
  ]
}
```

- `reviews.*` is one of `pending | running | PASS | CHANGES`, and it resets to `pending` whenever
  a new round starts.
- `gate` is `null` until the gate runs, then `{ "build": 0, "test": 0, "ui": 0, "at": "..." }`
  (process exit codes).
- `needsHuman` is `null` or `{ "reason": "<code from the table>", "detail": "<one line>" }`.
- `by` is `orchestrator`, `human`, `gate`, or `hook`.
- Ids are `J-NNN`, allocated by the script. Simulator jobs are `SIM-NNN`.

## `events.jsonl`

One JSON object per line, append-only. The dashboard builds its timeline and its "agent running
now" pulses from this file.

```json
{"ts":"2026-10-12T14:02:10Z","job":"J-001","kind":"agent-start","agent":"security-reviewer","round":1}
{"ts":"2026-10-12T14:04:55Z","job":"J-001","kind":"agent-stop","agent":"security-reviewer","round":1}
{"ts":"2026-10-12T14:05:00Z","job":"J-001","kind":"stage","from":"review","to":"approve"}
{"ts":"2026-10-12T14:07:30Z","job":"J-001","kind":"gate","detail":"build=0 test=0 ui=0"}
```

`kind` is one of `stage | agent-start | agent-stop | gate | merge | note`.

## Agents

| Agent | Model | Reads | Writes |
| --- | --- | --- | --- |
| spec-writer | opus | request.md, the repo | spec.md |
| builder | opus | spec.md, previous round's reviews, rework note | source on the job branch, round-n/build.md |
| security-reviewer | opus | spec.md, the diff, build.md | round-n/review-security.md |
| code-reviewer | sonnet | same | round-n/review-code.md |
| ux-reviewer | sonnet | same | round-n/review-ux.md |
| ui-reviewer | sonnet | same + docs/DESIGN.md | round-n/review-ui.md |
| approver | opus | spec.md, every build.md and review in the job | decision.md |

Reviewers inspect the diff (`git diff main...factory/<id>`) and re-run the tests themselves. They
treat `build.md`'s claims as claims, not as evidence.

## Gate

`factory.ps1 gate <id>` runs these on the job branch, in this order, and writes the output to
`gate.log`:

1. `dotnet build shelf.slnx`
2. `dotnet test tests/shelf.api.tests`
3. `npm run build` in `src/ui/react`

`factory.ps1 merge <id>` then requires the release checkout to be clean (`git status --porcelain`
empty) and runs `git merge --no-ff factory/<id>` on `main`. If the merge conflicts, the script
aborts the merge and moves the job to `needs-human / merge-blocked`.

# 02: Factory.Core, contract types and rules

**Status:** done, merged in PR #10 (2026-11-10), with Kurt's review folded in: one type per file,
interfaces in each project's `Abstractions` namespace, and `Json` renamed `PsJsonReader`. Proven
locally: 246 .NET tests (238 unit, 8 integration), gates Core 98%, Store 100%, Cli 92%; a sanitized real
`board.json` (CRLF, 93 KB) and `events.jsonl` (1,331 lines) round-trip byte for byte. **Branch:**
`feature/02-core`. **Depends on:** 01.

**Deviations (2026-11-10):**
- **The board stays a JSON document.** `Job`, `Idea`, `Flag` and `Deploy` are typed views over
  `JsonObject`, not records, so unknown keys and each job's own key order survive a write (the contract's
  "every write keeps the file's order"). `Transitions.Move` therefore updates the job in place and
  returns its `stage` event, as `Move-Job` does, rather than returning a new job.
- **Rules are pure; their inputs come from the caller.** The canary fingerprint (file hashes, HCP) is a
  `Func<string>`, verdicts and decisions arrive as text, and merge step 0 takes git's output. The
  set-stage orchestration (build checks 1–3 in order), `rework`, `redeploy` and `resume` are command
  bodies and land in 04 on top of `Glob`, `SpecText`, `Verdicts` and `MergeRules`.
- **Case.** Comparisons ignore case wherever `factory.ps1` used `-eq`, `-in` or `-match`, and stay exact
  where it used `-ceq` / `-cmatch` (verdict and rating lines).
- **Contract fix.** The escalation row now lists `gate` (factory.ps1 already escalated a gate that could
  not run); the table is checked against a hand-written copy of the contract's rows.
- **Coverage** skips source-generator output (`*.g.cs`, `[GeneratedRegex]`), which otherwise put Core at 84%.

## Goal

The contract as types and pure functions, with no I/O, so every later command is a thin shell over
it.

## Scope

From `docs/FACTORY-CONTRACT.md` and `factory.ps1`:
- **Types:** `Job`, `Idea`, `Stage`, `Flag`, `Review`, `Deploy` (status, attempts, urls,
  `destroyAt`…), `Canary`, `Event` with its kinds (`$EventKinds`), and verdict headers (reviews,
  decision). JSON round-trips `board.json` and `events.jsonl` exactly (property names, order where
  the tests care, `null`s, ISO times with milliseconds and `Z`).
- **The transition table** ("Job transitions", including `blocked`, `rework`, `redeploy`, the
  `set-stage` limits and the escalated → blocked rule for deployable jobs) as data, with
  `Move(job, to, by, reason)` returning the new job and the events, or a refusal with the exact
  one-line reason the PowerShell prints.
- **Rules:** `Require-Stage`, the gate and merge preconditions, `next` (runnable approved job;
  canary fingerprint), `Sweep-Due`, scope glob matching (`Scope: In`), and the verdict parsers
  behind `read-reviews` / `read-decision` (exit 2 on a malformed line).

## Tests

xUnit only, table-driven from the contract: every allowed transition and a sample of refused ones,
with the refusal text. Golden-file tests: real `board.json` and `events.jsonl` (sanitized copies
from `.factory-runs`) round-trip byte for byte.

## Done when

Every transition row has a test. No Pester changes, because nothing is forwarded yet.

# 02: Factory.Core, contract types and rules

**Status:** not started. **Branch:** `feature/02-core`. **Depends on:** 01.

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

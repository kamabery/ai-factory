# 13: EF Core store

**Status:** not started. **Branch:** `feature/13-ef-core`. **Depends on:** 12.

## Goal

The store of record becomes EF Core (SQLite locally, Azure SQL in cloud mode). `board.json` and
`events.jsonl` become exports, so humans and old tooling can still read them.

## Scope

- An EF Core model from `Factory.Core`, with `TenantId` on every table, and migrations.
- Transitions run in a transaction with optimistic concurrency. That replaces the lock file and the
  blob lease for these backends.
- A one-time importer from `.factory-runs/` (local) and from the Blob container (azure).
- Artifacts (specs, reviews, transcripts, evals) stay files: on disk locally, in Blob in the cloud.
  Only the board and events move into the database.
- Contract: "State backends" gains `sqlite` and `sql`; `local` and `azure` remain until a release
  drops them.

## Tests

The xUnit suite runs against every backend: SQLite in-memory, file, blob stub, and SQL Server
through a Testcontainers or LocalDB fixture.

## Done when

The factory runs a real job on SQLite, the importer has moved today's board with every job and
event intact (counts and a sample diff), and the dashboard is unchanged.

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

## Decisions (made in the spec's session)

- **Rows keep the board's JSON.** Each idea and job is a row holding its JSON object, with key columns
  (`Stage`, `Round`, `Target`, ...) copied beside it for queries, and the board's own keys are one
  skeleton row. So the exports are byte for byte what the file backend writes, keys added later need no
  migration, and `Factory.Core`'s JSON views stay the model.
- **Concurrency per row, not per board.** A save checks only the rows it writes, so the runner's moves and
  a human's drag on another card never refuse each other; a whole-board version would have refused a
  merge whose GitHub side had already happened. The cost is write skew across rows (a rule that reads one
  job and writes another is not checked against the first); no current transition depends on that.
- **An event is its own insert**, as it is its own append in the file backend: commands save and then add
  their events, and a crash between the two loses the same thing either way.
- **SQL Server tests use Testcontainers**, not LocalDB: Docker runs the same image on Windows and in WSL,
  and LocalDB is Windows only. The image is pinned (`mssql/server:2022-CU21-ubuntu-22.04`).
- **`sql` keeps artifacts in each machine's `FACTORY_HOME`.** How they are shared in the cloud (Blob sync or
  an Azure Files mount) is cloud mode's (18) to decide; until then, one machine per `sql` database.

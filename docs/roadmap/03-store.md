# 03: Factory.Store, file and blob backends

**Status:** not started. **Branch:** `feature/03-store`. **Depends on:** 02. **Decision:** D2.

## Goal

`IFactoryStore`, with the two backends the contract defines today ("State backends"), ported
faithfully: `local` (files in `FACTORY_HOME`) and `azure` (Blob container, `state.ps1`).

## Scope

- **`local`:** atomic `board.json` writes (temp file, then a rename retried for up to 2 s while a
  reader holds the file), `events.jsonl` appends, and the job/idea/session/eval/lesson files under
  `FACTORY_HOME`. Replace the named mutex with a cross-platform lock file plus the store's own
  retry, since the contract's lease semantics must hold.
- **`azure`:** the board lease (60 s, released on refusal), conditional GETs/PUTs on ETags,
  `.sync.json`, the cap and prune (blob versions; needs Storage Blob Data Owner), and the token cache
  outside `FACTORY_HOME`. Auth through `Azure.Identity` (`DefaultAzureCredential`), with no keys or
  SAS, matching `docs/STATE.md`.
- **`TenantId`** on the store interface from day one (always `default`). It is the hosted seam and
  is not written to `board.json`.
- The blob stub (`factory/tests/fixtures/blob-stub.ps1`) stays the test server. The .NET client
  must talk to it (the endpoint is overridable, as `state.ps1` does).

## Tests

`state.Tests.ps1` and `state-factory.Tests.ps1` pass with `FACTORY_IMPL=dotnet` where they drive
commands; xUnit covers the lease, ETag conflicts, rename retry, cap and prune against the stub.

## Done when

`status` (from 01) runs on both backends under .NET, and the xUnit store tests are green on three
OSes.

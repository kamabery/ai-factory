# 03: Factory.Store, file and blob backends

**Status:** done, merged in PR #11 (2026-11-10). D2 taken as recommended: the file and
blob backends ported as they are, EF Core in 13. Proven locally: 334 .NET tests (303 unit, 31
integration, 23 of them new against `blob-stub.ps1`), gates Core 98.0%, Store 98.7%, Cli 94.2%; the
`Ported` Pester blocks pass under both implementations on both backends, including the whole of
`state-factory.Tests.ps1` (two machines on one container, with the .NET `status` among
`factory.ps1`'s commands). **Branch:** `feature/03-store`. **Depends on:** 02. **Decision:** D2.

**Deviations (2026-11-10):**
- **The named mutex stays.** `factory-board-<hash>` is a named mutex, and .NET named mutexes work
  across processes on every OS. pwsh is a .NET process too, so while the port is part-way it is the
  only lock that keeps a forwarded command and `factory.ps1` off the board at the same time. A lock
  file would let both write at once. The contract's lease semantics hold unchanged.
- **Blob REST over `HttpClient`, not the Azure SDK.** The SDK refuses a bearer token over plain
  http, so it could not talk to the stub. `BlobClient` makes `state.ps1`'s calls one for one, with
  the same retries and error text.
- **The token chain is `state.ps1`'s, not `DefaultAzureCredential`.** It tries, in order,
  `FACTORY_STATE_TOKEN`, the managed identity endpoint, the cached `az` token, then `az` itself
  (or `FACTORY_AZ_CMD`). The cache file lets the next process skip `az`, which a per-process
  credential cannot, and `FACTORY_AZ_CMD` is the tests' seam. `Azure.Identity` is for 18 (cloud
  mode), where there is a real managed identity to prove it on.
- **The store is a session.** `IFactoryStore.Open()` takes the lock and reads the board. The
  `IBoardSession` it returns saves the board, adds events, and ends with `Close()` (the push, prune
  and note) before `Dispose` releases the lock. `CliApp` runs every command inside a session, as
  `factory.ps1`'s body does, so `ICommand.Run` takes the session. `TenantId` is `default`.
- **Left for the specs that use them:**
  - the lease keepers, to 07 (runner, canary, idea turns);
  - `Sync-Events` and the dashboard's polling refresh, to 08;
  - `state-migrate`, `state-pull`, `prune` and `allow-ip`, to 11;
  - the agent-stop transcript copy, to 04 (`event`);
  - re-taking the lock after a long step, to 06 and 09.
- **No `.env` outside a repository.** `factory.ps1` refuses there even with `FACTORY_HOME` set,
  because it always looks for `.env`. The CLI treats it as no `.env`.
- **"Three OSes"** is now Windows, locally: tests run before a PR, never in CI.

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

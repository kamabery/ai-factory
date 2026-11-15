# 14: `factory init` / `doctor`, the GitHub App and secrets

**Status:** done, in PR #24 (2026-11-15). .NET 957 unit, 190 integration + 1 skipped, gates green (Windows). Live
proof on a simulated clean profile (fresh home, a .env init created with no GITHUB_TOKEN, a fresh Credential Manager
prefix; the claude login carried over): `factory init` created, installed and authorized the GitHub App
`ai-factory-kamab`, doctor all green, and J-951 went from idea to a reviewed PR (todoapp-two #19) opened by
`app/ai-factory-kamab`, eval good. Decided in the session: the App's user token creates repos (falling back to "create it
yourself"); Key Vault waits for 18; the PAT stays a fallback until 15. Found on the way: the manifest's webhook block and
permission name, init's serve fell back to the main .env, HCP's safe-delete content type, the runner test's race. **Branch:** `feature/14-init`. **Depends on:** 13. **Decision:** D1 (name)
should be made first.

## Scope

- **`factory init`:** checks `git`, `dotnet`, `claude` (and `az` only when Foundry is chosen), and
  prints each OS's install command for anything missing. Then it sets up the GitHub App, the LLM and
  the first target, and starts and opens the dashboard.
- **GitHub App via the manifest flow:** the CLI serves a localhost callback, opens the browser on a
  manifest whose permissions are defined in the repo, and stores the App ID and private key. It
  mints an installation token per job, behind the 05 `IGitHub` credential seam. The GitHub PAT path
  remains as a fallback until 15. `docs/GITHUB-PAT.md` becomes the GitHub App doc.
- **LLM:** detect a Claude Code subscription login (local); or `--foundry`: provision or choose the
  Foundry resource with pinned deployments (reuse `factory/infra/llm`), assign Azure AI User to the
  signed-in user, keyless.
- **`ISecretStore`:** Windows Credential Manager, macOS Keychain, libsecret; Key Vault plus managed
  identity in the cloud. `.env` stops holding secrets (it may keep non-secret settings).
- **`factory doctor`:** for each credential and permission (GitHub App installation and repo access,
  LLM credential, a 1-token request against each pinned deployment, the store, and, if configured,
  HCP and Azure) it prints pass or fail and the fix. It absorbs the `github-permission` diagnosis.

## Done when

On a clean Windows user profile, `factory init` followed by one idea reaches a reviewed PR with no
`.env` edits, and `factory doctor` is all green. xUnit covers the manifest exchange (against a stub)
and each doctor check.

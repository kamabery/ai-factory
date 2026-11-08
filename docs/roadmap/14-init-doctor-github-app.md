# 14: `factory init` / `doctor`, the GitHub App and secrets

**Status:** not started. **Branch:** `feature/14-init`. **Depends on:** 13. **Decision:** D1 (name)
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

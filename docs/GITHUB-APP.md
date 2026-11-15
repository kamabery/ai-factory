# The GitHub App

AI factory reaches GitHub as a **GitHub App** of your own, which `factory init` creates and installs. Its keys
live in your operating system's secret store, never in `.env`. Only `factory` uses them; agents never see a
token and never push. The rules are in "Targets" and "Secrets, init and doctor" in
[FACTORY-CONTRACT.md](FACTORY-CONTRACT.md).

- **Installation token:** a one-hour token for the target's owner, minted from the App's key, used for every
  GitHub call but one.
- **User token:** used only to create a new repo, which an App installed on a personal account cannot do as
  itself. It acts as you, within the App's permissions, and is refreshed as needed.

## Set it up

```powershell
factory init                 # the App on your own account
factory init --org <org>     # or on an organization you administer
```

`init` opens your browser three times. Each time, you click once:

1. **Create GitHub App.** GitHub shows the App's manifest (`factory/github-app.json`): its name, `AI factory
   (<owner>)`, and the permissions below. Rename it if the name is taken.
2. **Install**, on **All repositories**. A new target repo has to be on the installation's list, and a
   selected-repositories install would leave it off (init refuses one).
3. **Authorize**, so the App can create repos as you.

`init` stores `GITHUB_APP_ID`, `GITHUB_APP_SLUG`, `GITHUB_APP_CLIENT_ID`, `GITHUB_APP_CLIENT_SECRET`,
`GITHUB_APP_PRIVATE_KEY` and the user token in the secret store:
- Windows: Credential Manager, `ai-factory/<NAME>`.
- Linux: libsecret.

It writes `GITHUB_OWNER` to `.env`. Run it again whenever you like: each step it already did is skipped.

## What the factory does with it

The App asks for these repository permissions (Metadata read is added automatically):

| Action | When | Permission |
| --- | --- | --- |
| List the installation's repos | New idea's repo picker (`list-repos`) | Metadata: read |
| Check a target exists and has `main` | New idea, `check-target` | Metadata: read |
| Create a repo (private), with the user token | First checkout of a new target's bootstrap job | Administration: write |
| Clone, push `factory/<id>`, delete the branch | Every job | Contents: write |
| Open, close and merge the job's PR | Ready, Discarded, Done | Pull requests: write |
| Push a branch that changes `.github/workflows/` | Any job that touches a workflow; every scaffold has `deploy.yml` | Workflows: write |
| Dispatch `deploy.yml` and read its runs and job logs | The deploy stage ([DEPLOY.md](DEPLOY.md)) | Actions: write |
| Create the `preview` environment and set its variables | The deploy stage | Environments: write · Variables: write |
| Set the `TF_API_TOKEN` secret (through `gh`, the token in its environment only) | The deploy stage | Secrets: write |

## Check it

```powershell
factory doctor
```

```
ok    tools: git 2.47.1, dotnet 10.0.402, claude 2.1.325
ok    secret-store: Windows Credential Manager (ai-factory/...)
ok    env-secrets: no secrets in .env
ok    github: the App ai-factory-kamabery on kamabery (all repositories), signed in as kamabery
ok    llm: local mode: claude is logged in to a claude.ai subscription
```

Every `warn` or `fail` line is followed by a `fix:` line that says what to do.

## When it goes wrong

| doctor says | Fix |
| --- | --- |
| `github: the App ... is not installed on <owner>` | Open the install link it prints; choose **All repositories** |
| `... is installed on selected repositories` | The installation's settings page (in the fix line): Repository access → **All repositories** |
| `the installation on <owner> lacks <permission>` | The App's permissions changed (`github-app.json`): accept them on the installation's settings page |
| `github: GitHub 401 (GET /app)` | The App or its key is gone: run `factory init` again |
| A job escalated with `GitHub refused to create <repo> (...); create it, empty, then approve the job again` | Create the repo yourself, private and empty, on the owner; then approve the job again |
| `env-secrets: still in .env: GITHUB_TOKEN, ...` | `factory init --move-env-secrets` copies them to the store and comments them out |

A `github-permission` flag on a deploy names the permission the App lacks. Add it in the App's settings
(GitHub → Settings → Developer settings → GitHub Apps → *the App* → Permissions), then accept it on the
installation.

## The GitHub PAT fallback

Until spec 15, a factory with no App (no `GITHUB_APP_ID`) still signs in with a fine-grained **GitHub PAT**,
`GITHUB_TOKEN`. `doctor` warns about it (`using the GitHub PAT fallback`). To keep using one, create it under
GitHub → **Settings → Developer settings → Personal access tokens → Fine-grained tokens**:

- **Resource owner:** the owner of your targets (`GITHUB_OWNER`).
- **Repository access:** **All repositories**. With *Only select repositories*, the PAT can create a repo but
  can't push to it, as J-012 found.
- **Permissions:** the ones in the table above, read and write.

Put it in the secret store, or in `.env` as `GITHUB_TOKEN=github_pat_...`; `factory init --move-env-secrets` moves
it from `.env` to the store.

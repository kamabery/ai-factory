# Setting up the GitHub PAT

The factory reaches GitHub with one fine-grained **GitHub PAT** (personal access token), stored as
`GITHUB_TOKEN` in `.env` at the repo root. Only `factory.ps1` reads it. Agents never see it and never
push. See "Targets" in [FACTORY-CONTRACT.md](FACTORY-CONTRACT.md) for the rules.

## What the factory does with it

| Action | When | Needs |
| --- | --- | --- |
| List your repos | New idea's repo picker (`list-repos`) | Metadata: read |
| Check a target exists and has `main` | New idea, `check-target` | Metadata: read |
| Create a repo (private) | First checkout of a new target's bootstrap job | Administration: write |
| Push the empty initial commit, clone, push `factory/<id>`, delete the branch | Every job | Contents: write |
| Open, close and merge the job's PR | Ready, Discarded, Done | Pull requests: write |

## Create it

GitHub → **Settings → Developer settings → Personal access tokens → Fine-grained tokens →
Generate new token**.

| Setting | Value |
| --- | --- |
| Token name | `claude-factory` |
| Resource owner | You, or the organization that owns your targets (the same as `GITHUB_OWNER`) |
| Expiration | Your call. When it expires, every GitHub call fails with GitHub 401 |
| **Repository access** | **All repositories** |
| Repository permissions | **Administration**: Read and write · **Contents**: Read and write · **Pull requests**: Read and write · Metadata: Read (added automatically) |
| Account permissions | None |

**Why All repositories:** with *Only select repositories*, the PAT can create a repo, but the new repo
isn't on its list, so it can't push to it. The bootstrap job then fails on its first push, as J-012 did:

```
remote: Write access to repository not granted.
fatal: ... The requested URL returned error: 403
```

If you must use *Only select repositories*, create each new target repo yourself, add it to the
PAT's list, and only then approve its bootstrap job.

**For an organization:** the organization has to allow fine-grained PATs, and may need to approve
this one before it works. Set Resource owner to the organization.

## Put it in `.env`

```dotenv
GITHUB_TOKEN=github_pat_...
GITHUB_OWNER=your-user-or-org
```

`.env` is gitignored. Start from `.env.example`. A change takes effect without a restart:
`factory.ps1` runs fresh for each call, and `serve.ps1` reads `.env` on each request.

## Check it

```powershell
pwsh factory/scripts/factory.ps1 list-repos                  # your repos as JSON: read access works
pwsh factory/scripts/factory.ps1 check-target <owner>/<repo>  # 'existing' or 'new'
```

Neither command writes anything. Write access only shows when a job pushes. To test it beforehand
without changing anything, try a dry-run push to one of your repos from an empty commit:

```powershell
$pat = (Select-String .env -Pattern '^GITHUB_TOKEN=(.*)$').Matches[0].Groups[1].Value
$basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$pat"))
$tmp = Join-Path $env:TEMP "pat-check"; git init -q -b main $tmp
git -C $tmp -c user.name=x -c user.email=x@x commit -q --allow-empty -m check
git -C $tmp -c credential.helper= -c "http.extraHeader=Authorization: Basic $basic" push --dry-run https://github.com/<owner>/<repo>.git main:pat-check
Remove-Item -Recurse -Force $tmp
```

A `403` here means the PAT can't push to that repo.

## When it goes wrong

| You see | Cause | Fix |
| --- | --- | --- |
| Job escalated, `work dir: initial commit for <repo> failed: ... 403` | The PAT can create repos but not push to new ones (*Only select repositories*) | Set Repository access to All repositories, then drag the job to Approved with a note |
| `cannot create <repo>: GitHub 403: Resource not accessible by personal access token` | No Administration: write, or the wrong Resource owner | Add the permission, or regenerate the PAT under the right owner |
| `push failed: ... 403` on publish, or `merge blocked: GitHub 403` | No Contents or Pull requests write | Add the permission |
| `GitHub 401` on everything | The PAT expired or was revoked | Regenerate it and update `.env` |
| `GitHub 0: GITHUB_TOKEN is not set in .env` | `.env` is missing, or `GITHUB_TOKEN` is empty | Fill it in |
| New idea's picker says it couldn't list your repos | Any of the above | Run `factory.ps1 list-repos` to see GitHub's message |

# claude-factory

A Claude Code software factory: spec → build → four parallel reviewers → approver → deterministic
gate → merge. It is installed into **Shelf**, a small .NET 10 + Aspire + React app, and a live
dashboard shows each job moving through the pipeline.

- **Status:** the workspace is scaffolded. The factory itself gets built by following [docs/BUILD-GUIDE.md](docs/BUILD-GUIDE.md).
- **Contract:** [docs/FACTORY-CONTRACT.md](docs/FACTORY-CONTRACT.md) covers stages, files, board and event formats.
- **Rules for agents:** [CLAUDE.md](CLAUDE.md).

## Layout

```
.claude/agents/            7 factory agents (step 3)
.claude/skills/factory/    /factory orchestrator skill (step 4)
factory/backlog.md         seed features
factory/templates/         spec / build / review / decision formats
factory/scripts/           factory.ps1 (control plane), serve.ps1, simulate.ps1, run-job.ps1
factory/dashboard/         index.html, the live view
factory/tests/             Pester + Playwright
.factory-runs/             runtime state (gitignored)
src/ tests/ contracts/     Shelf: shelf.api, shelf.data, sqlite migrations, servicedefaults, ui/react
.aspire/apphost.cs         file-based AppHost (api + ui)
IaC/                       Terraform for Azure (from the scaffold)
```

## Shelf

Reproduce the scaffold with:

```
dotnet new aspire-base     -n Shelf -o claude-factory --dbProvider sqlite --resource Book
dotnet new aspire-ui-react -n Shelf -o claude-factory --resource Book
```

Run it with `aspire run`; there is no Docker requirement because the store is SQLite. The API serves
Swagger at `/api-docs` and returns 404 at `/`. The UI has `/books`, plus `/styleguide`, which
renders every approved component; `docs/DESIGN.md` holds the rules behind it.

Deployment is in `IaC/README.md`: Container Apps for the API, a Static Web App for the React UI,
and `.github/workflows/deploy.yml` with three OIDC secrets.

# AI factory

A software factory built on Claude Code, with a human at both ends. You approve the spec and accept the result; agents
do everything in between. Claude grills an idea into small specs, then each job is built, checked by four reviewers in
parallel (security, code, UX, UI) and an approver, and gated by commands you approved with the spec. What lands is a
pull request on your GitHub repo, and a dashboard, on your machine or in Azure, is where you work it.

A deterministic .NET control plane (the `factory` CLI) owns state, the gate and the merge, so an agent never moves a
card, edits what judges it, or pushes. Only you accept a job: a drag to Done merges its PR.

## What you need

The **core tier** is GitHub plus an LLM:

| | |
| --- | --- |
| **.NET 10 SDK** | `winget install --id Microsoft.DotNet.SDK.10 -e`, or on Linux `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash -s -- --channel 10.0` |
| **git**, with your identity | `winget install --id Git.Git -e`, or `sudo apt-get install -y git`; then `git config --global user.name "<your name>"` and `user.email`, since the builder commits as you |
| **Claude Code**, signed in to a Claude subscription | `irm https://claude.ai/install.ps1 \| iex` (Windows), or `curl -fsSL https://claude.ai/install.sh \| bash`; then `claude` and `/login` |
| **A GitHub account** | `factory init` creates the factory's GitHub App on it; there is no token to make |

No PowerShell 7, Node or Docker is needed: a spec's gate lines run as plain programs. Whatever your target repo builds
with (Node for a React front end, say) has to be installed too, since the gate runs it. Windows and Linux (Ubuntu
24.04) are supported; macOS is not, yet. On Linux the secrets go to the desktop keyring through `secret-tool`:
`sudo apt-get install -y libsecret-tools`.

Instead of a subscription, the LLM can be **Claude in Microsoft Foundry**, keyless through Entra ID (`factory init
--foundry <resource>`, with `az login`); see [docs/FOUNDRY.md](docs/FOUNDRY.md).

## Getting started

```bash
dotnet tool install -g ai-factory
factory init
```

`init` checks the tools, then opens your browser to create the factory's **GitHub App** (private, no webhook), install
it on your account (All repositories) and sign you in to it. Its keys go to your OS secret store (Windows Credential
Manager, or libsecret on Linux), never to a file. It writes `GITHUB_OWNER` to `~/.ai-factory/.env`, clones the
templates new projects are scaffolded from, runs `factory doctor`, and opens the dashboard.
[docs/GITHUB-APP.md](docs/GITHUB-APP.md) has the details.

Then, in the dashboard:
1. **New idea**: pick a target repo (`owner/name`) and give a sentence. For a small first job, fork
   [kamabery/ai-factory-hello](https://github.com/kamabery/ai-factory-hello), a .NET console app and its tests.
   Or pick **+ New repo** to make an app from scratch: see "Two kinds of idea" below.
2. Answer Claude's questions until the brief is right, and approve the specs it proposes.
3. Wait for the card to reach **Ready for review**, read its PR, and drag it to **Done**.

`factory doctor` says what is wrong with a machine, and how to fix it. `dotnet tool update -g ai-factory` updates the
factory.

## Two boards: on your machine, or in Azure

The factory runs either way, and every feature works on both. Each is a whole factory with its own board, so pick by
where you want jobs to run, and run both if you like: nothing makes the choice for good.

| | **Local board** (`factory serve`) | **Azure board** (`factory init cloud`) |
| --- | --- | --- |
| Runs on | this machine: the dashboard and one runner, while it is on | Azure Container Apps: the dashboard scales to zero, one runner job execution per job |
| LLM | your Claude subscription (or Foundry, with `--foundry`) | Claude in Microsoft Foundry, keyless through a managed identity |
| Board state | files in the factory's home (or SQLite, or Blob storage: [docs/STATE.md](docs/STATE.md)) | Azure SQL serverless |
| Cost | nothing beyond your subscription | about $1–6 a month idle, plus about $0.20 of container time and the Foundry tokens per job |
| Needs | the core tier above | an Azure subscription, previews set up first, Terraform |
| Choose it when | you are trying it out, or you work at this machine | jobs should run with your machine off, or a subscription must not power unattended work |

Start local: everything above is the local board. [docs/CLOUD.md](docs/CLOUD.md) deploys the Azure board, and the
dashboard's header says which one you are on (`local · subscription` or `cloud · foundry`).

## Working the board

Ideas are grilled in the panel on the `claude` CLI, headless, like a job (`claude-opus-5-5` unless
`FACTORY_IDEA_MODEL` says otherwise). Claude asks rounds of clickable questions until nothing in the idea is assumed,
reads the brief back for your yes, and splits it into one to five small specs. Each spec card asks you to approve it,
change it (edit it, or ask Claude), choose "not yet", or reject it. A "not yet" spec waits on its idea's card in the
**Ideas** column, with **Approve** and **Edit**, and the idea stays open until it is decided; the board has no Backlog
column. The panel shows the session's cost as it goes. Closing the browser loses nothing: the session's card reopens it.

**Two kinds of idea.** A **feature** idea builds something on a repo that exists. A **New repo** idea (**+ New repo**
in the panel) makes the app: Claude grills you on its name and scaffold, what it is for, who uses it, what it opens on
at `/` (its Home), and the features to build, 1 to 10 in order. You confirm that as a product document, and a
bootstrap job scaffolds the repo from the templates with it committed as `docs/PRODUCT.md`. The repo's card in Ideas
goes Grill → Scaffold → Review → Merged; once you merge the scaffold it offers **Implement first feature**, then
**Implement** each next one in order, every one a feature idea of its own whose session starts from that feature's
text (the first one's specs carry the Home decision). **Dismiss** stops offering them. From the CLI it is
`factory idea <owner/name> --feature <k>`.

**Approved** is a FIFO queue, and jobs build one at a time, each in its own temp clone that is deleted when the job is
done or discarded. With previews on, a job that would deploy while the current templates and deploy code have no
**canary** pass waits in the **Canary** column first: the canary deploys a throwaway scaffold the way a preview would,
so a broken template fails there, not in your job ([docs/DEPLOY.md](docs/DEPLOY.md), "The canary"). A "canary due"
chip in the header says when that will happen, and why. A building card's pipeline strip lights up as each agent runs:
1. **builder:** implements the spec on branch `factory/<id>`.
2. **security, code, UX and UI reviewers:** in parallel. Any `CHANGES` sends the job back to the builder for round 2.
3. **approver:** checks every acceptance criterion against the evidence.
4. **gate:** the spec's gate commands (`dotnet build`, `dotnet test`, ...) must all exit 0.

The job then lands in **Ready for review** with a pull request. Read the diff, the reviews and the decision in the
drawer, then:
- **drag to Done** to merge the PR. This is the only way anything merges; merging the PR on GitHub counts too;
- **Rework**, with a note, to send it back to the queue for another build;
- **Discard** to drop the job, close its PR and delete its branch.

A red badge on a card says why the factory gave up or refused:

| Badge | Meaning | What to do |
| --- | --- | --- |
| `max-rounds` | reviewers still want changes after round 2 | read the reviews; rework, or drag to Done to overrule them |
| `escalated` | the approver or an agent needs a human decision | read `decision.md`; rework or discard |
| `gate-failed` | a gate command failed | rework; Done is refused until the gate is green |
| `merge-blocked` | nothing to merge, a dirty work dir, or GitHub won't merge the PR | read the detail, fix it, drag to Done again |

`factory status` prints the board as text.

## Previews (an add-on)

With HCP Terraform and an Azure subscription, each job's branch can be deployed to an ephemeral Azure preview before the
approver, for you to QA from Ready for review. Previews are off until `.env` has `HCP_ORG`. Their one-time setup is
Terraform (`factory/infra/previews`, applied by you under `az login`), then `factory previews-env` copies its outputs
into `.env` and `factory doctor` checks them; the HCP organization and, for sign-in, the Entra External tenant are made
by hand. See [docs/DEPLOY.md](docs/DEPLOY.md).

## Changing the factory itself

**Use this template** on GitHub makes your own copy, whose plugin, agents and templates you can change. Run it from a
checkout:

```bash
dotnet build src/factory   # then put artifacts/bin/Factory.Cli/debug on PATH as `factory`
factory init
```

A checkout keeps its `.env` and state (`.factory-runs/`, gitignored) in the repo. Before a pull request, run the tests
locally ([docs/TESTING.md](docs/TESTING.md)); the repo has no CI:

```bash
dotnet run --project src/factory/Factory.TestGates                   # unit + integration, tier counts, coverage gates
dotnet run --project src/factory/Factory.Simulator -- --reset        # fake jobs on the dashboard, no tokens spent
cd factory/tests/dashboard && npm ci && npx playwright test --grep @smoke
```

[docs/FACTORY-CONTRACT.md](docs/FACTORY-CONTRACT.md) is the source of truth for stages, transitions, files and
formats, and [CLAUDE.md](CLAUDE.md) holds the rules for agents working on the factory.

## Layout

```
factory/plugin/      the agents, skills (/factory, /factory:idea, grill) and hooks
factory/templates/   spec, build, review and decision formats
factory/dashboard/   the dashboard page
src/factory/         the factory CLI, the runner, the dashboard server, the AppHost, tests, stubs and the simulator
factory/scripts/     the preview setup scripts (PowerShell, until previews have their own init)
factory/infra/       Terraform: the Foundry account, Blob state storage and a budget alert
docs/                the contract, testing, GitHub App, deploy and Foundry guides, and the roadmap
```

## License

[MIT](LICENSE).

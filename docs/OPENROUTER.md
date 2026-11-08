# Running jobs on OpenRouter

A job whose **provider** is `openrouter` runs its `claude` pass, with its builder, reviewers and
approver, against OpenRouter's Anthropic-compatible API instead of your Claude subscription. It
uses the same Claude models, so a job's result can be compared with a subscription run. See
"Providers" in [FACTORY-CONTRACT.md](FACTORY-CONTRACT.md) for the rules.

## Create the key

OpenRouter → **Keys → Create key**.

| Setting | Value |
| --- | --- |
| Name | `claude-factory` |
| **Credit limit** | Set one (e.g. $20). An agent can read its own environment despite the deny rules, so the limit is what bounds a leaked key |

The account also needs credits. A key with a limit but an empty account fails with `402 ... can
only afford N tokens`.

Under **Settings → Provider routing**, put **Anthropic** first. OpenRouter guarantees Claude Code
only on Anthropic's own endpoint.

Put the key in `.env` as `OPENROUTER_API_KEY=...`. Only the runner reads it, and only an `openrouter`
job's `claude` process gets it.

## Use it

- **One job:** click the provider chip on a Backlog or Approved card to switch between
  `subscription` and `openrouter`, or run `factory.ps1 set-provider <id> openrouter`.
- **Every new job:** set `FACTORY_PROVIDER=openrouter` in `.env`.

The chip stays on the card after the job leaves Approved when the provider is `openrouter`.

## What changes for an openrouter job

- `claude` runs with `ANTHROPIC_BASE_URL=https://openrouter.ai/api`, `ANTHROPIC_AUTH_TOKEN=<key>`
  and an empty `ANTHROPIC_API_KEY`. The token takes precedence over your claude.ai login without
  a `/logout`, and claude.ai connectors are off for that pass.
- The aliases `opus`, `sonnet` and `haiku` resolve to Claude model ids that OpenRouter accepts. To
  pin others, set `ANTHROPIC_DEFAULT_{FABLE,OPUS,SONNET,HAIKU}_MODEL` or `CLAUDE_CODE_SUBAGENT_MODEL`
  in `.env`; they are passed to openrouter jobs only.
- The card's cost is `claude`'s own estimate from list prices. The `cost` event records
  `provider`, so it can be checked against OpenRouter's **Activity** page. In the first check, a
  one-line `claude -p`, both said $0.10.
- A missing key escalates the job (`provider: no OPENROUTER_API_KEY in .env`) before anything runs.

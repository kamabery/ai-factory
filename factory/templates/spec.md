# Spec: <job-id> — <title>

## Intent
<One paragraph: what the user wants and why, in the requester's words.>

## Target
<!-- Copied verbatim from the idea. A path that does not exist yet becomes a new git repo. -->
- Repo: <owner/name>

## Gate
<!-- The commands the gate runs in the job's clone of the target, in order. Each must exit 0.
     Each is one program and its arguments, run with no shell: no ; | && > $ cd or PowerShell.
     Use a tool's directory flag (npm --prefix src/ui run build), and factory check file <path>
     [--heading <re>] [--contains <text>] [--matches <re>] to check a file. -->
- build: `<command>`
- test: `<command>`

## Acceptance criteria
<!-- Each one observable, and each named with the test or command that proves it. -->
- [ ] AC1: <behaviour> — proven by `<test name or command>`
- [ ] AC2: ...

## Home
<!-- New app only (its idea scaffolded the repo), in the one spec that owns the front door: the brief's Home,
     as one line. replace: what `/` shows instead; redirect: the route `/` sends to (`/books`); keep: the
     scaffold's dashboard, and why. Scope must cover the files it touches. Delete this section in an
     existing repo's spec and in a new app's other specs. -->
- Home: <replace | redirect | keep>: <detail>

## Scope
<!-- One sub-bullet per path the builder may touch: a backticked repo-relative glob first, prose after.
     `*` stays within one folder, `**` crosses folders. The factory compares the build's diff with these
     globs and stops the job on any other path, so list generated outputs (lockfiles,
     contracts/api.openapi.json, routeTree.gen.ts) wherever the change will regenerate them. Two paths on one
     line are comma-joined at its start (`a.ts`, `b.ts` (why)); a path after other words ("`a` and `b`") is
     refused, since the check would miss it. -->
- In:
  - `src/<name>.api/Controllers/**` (the endpoint)
  - `tests/<name>.api.tests/**/*.cs`
  - `contracts/api.openapi.json` (generated)
- Out: <explicitly excluded>

## Risks
<Security, data, contract or UX risks the reviewers should look at first.>

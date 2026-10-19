# Spec: <job-id> — <title>

## Intent
<One paragraph: what the user wants and why, in the requester's words.>

## Target
<!-- Copied verbatim from the idea. A path that does not exist yet becomes a new git repo. -->
- Repo: <owner/name>

## Gate
<!-- The commands the gate runs in the job's clone of the target, in order. Each must exit 0. -->
- build: `<command>`
- test: `<command>`

## Acceptance criteria
<!-- Each one observable, and each named with the test or command that proves it. -->
- [ ] AC1: <behaviour> — proven by `<test name or command>`
- [ ] AC2: ...

## Scope
- In: <files and areas the builder may touch>
- Out: <explicitly excluded>

## Risks
<Security, data, contract or UX risks the reviewers should look at first.>

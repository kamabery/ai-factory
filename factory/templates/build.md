# Build: <job-id> — round <n>

Branch: factory/<job-id>
Commits: <sha list>

## Merge from base
<`git merge --no-edit factory/base`: "already up to date", "clean", or each conflicted file and how
both sides were kept.>

## Spec against base
<!-- Only when the orchestrator says the job's base moved (a sibling job merged). Otherwise delete
     this section. The second line must be exactly `Spec holds: yes` or `Spec holds: no`. -->
Spec holds: <yes | no>
<Why, from what the merged job changed. If no: the `Scope +: <glob>, ...` line and the AC wording
the spec needs, for the human to rework with.>

## What changed
<Files and the reason for each.>

## Evidence
<!-- Paste command + exit code. Claims without output are not evidence. -->
- `dotnet test tests/shelf.api.tests` → exit <code>
- `npm run build` (src/ui/react) → exit <code>

## Acceptance criteria status
- AC1: <met / not met> — <where it is proven>

## Rework notes addressed
<Only in rounds after 1: each CHANGES item or human rework note, and what was done about it.>

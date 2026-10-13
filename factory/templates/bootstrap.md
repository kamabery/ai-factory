# Spec: Bootstrap {{name}} from template-projects

## Intent
Start the new project `{{name}}` from the `template-projects` templates, so later feature jobs work
on a known scaffold. The human chose these options in the idea form: resource `{{resource}}`, data
store `{{db}}`, front end `{{ui}}`, events `{{events}}`. Nothing beyond the scaffold is built here;
the idea's features are specced after this job is merged.

Follow `{{templates}}/.claude/skills/scaffold-aspire-app/SKILL.md`, **Phases 3 to 5 only**, using
the answers above. Do not interview anyone and do not run Phase 2: the template pack is already
installed (`dotnet new list aspire-base` shows it). If it is not installed, give up and say so.
The template workspace is read-only.

## Target
- Repo: {{target}}

## Gate
{{gate}}

## Acceptance criteria
- [ ] AC1: These commands were run verbatim, in order, in the worktree root, and each exited 0:
{{commands}}
  — proven by the commands and their exit codes in build.md.
- [ ] AC2: Every `.scaffold/*.wiring.md` was applied exactly as written (markers kept, blocks inserted
  where they say), and `.scaffold/` is deleted — proven by `git ls-files .scaffold` printing nothing
  and the wiring blocks being present in the diff.
- [ ] AC3: `README.md` and `CLAUDE.md` exist and describe only what was generated, per the skill's
  Phase 4 — proven by the files in the diff.
- [ ] AC4: Every Gate command exits 0 — proven by the Gate commands' output in build.md.

## Scope
- In: everything the templates generate, the wiring edits, README.md, CLAUDE.md, `.gitignore`.
- Out: any feature beyond the scaffold's sample `{{resource}}` slice; any change to the template
  workspace; Terraform apply or any deployment.

## Risks
- **Wiring:** a block missed or applied from memory instead of from the wiring file; `--resource`
  or `-n` differing between template runs fails silently.
- **Secrets:** no connection strings or keys committed beyond the templates' local defaults.
- **Generated files:** `contracts/api.openapi.json` and the generated TS types come from the
  templates; they must not be hand-edited.

# Spec: Bootstrap {{name}} from template-projects

## Intent
Start the new project `{{name}}` from the `template-projects` templates, so later feature jobs work
on a known scaffold. The human chose these options in the idea form: resource `{{resource}}`, data
store `{{db}}`, front end `{{ui}}`, events `{{events}}`, sign-in `{{auth}}`. Nothing beyond the scaffold is built here;
the idea's features are specced after this job is merged.

Invoke the `/aspire-starter:boot-strap-app` skill with the Skill tool (if it is not available, read
`{{templates}}/.claude/skills/boot-strap-app/SKILL.md` and follow it). Every answer is supplied, so
use its **answers-supplied** mode: name `{{name}}`, output directory `.` (the work dir root),
resource `{{resource}}`, data store `{{db}}`, events `{{events}}`, front end `{{ui}}`, sign-in
`{{auth}}`. Do not
interview anyone and do not install the template pack or `aspire-addon`; if either is missing, give
up and say so. **Skip Phase 5** (graphify) and list it as a follow-up in build.md. The template
workspace is read-only. Commit each `dotnet new` and each `aspire-addon add` on its own, with a
subject starting `scaffold: ` or `addon: ` (for example `addon: auth`), so later jobs can tell
template code from the app's own.

## Target
- Repo: {{target}}

## Gate
{{gate}}

## Acceptance criteria
- [ ] AC1: These commands were run verbatim, in order, in the work dir root, and each exited 0:
{{commands}}
  — proven by the commands and their exit codes in build.md.
- [ ] AC2: `aspire-addon` did the wiring: no `Refused:` in its output, no wiring applied by hand,
  and `.aspirestarter.json` lists every add-on run — proven by its report in build.md, the
  manifest in the diff, and `git ls-files .scaffold` printing nothing.
- [ ] AC3: `README.md` and `CLAUDE.md` exist and describe only what was generated, per the skill's
  Phase 4 — proven by the files in the diff. No `graphify-out/` and no `.claude/` hooks are committed.
- [ ] AC4: Every Gate command exits 0 — proven by the Gate commands' output in build.md.
- [ ] AC5: If the branch has `docs/PRODUCT.md` (the factory's `docs: product` commit, the app's roadmap from its
  New repo idea), it is unchanged: features are built later, one idea each — proven by `git diff <that commit> -- docs/PRODUCT.md`
  printing nothing.

## Scope
- In: everything the templates generate, the wiring edits, README.md, CLAUDE.md, `.gitignore`, `docs/PRODUCT.md`
  (the factory's commit, left unchanged).
- Out: any feature beyond the scaffold's sample `{{resource}}` slice; any change to the template
  workspace; Terraform apply or any deployment.

## Risks
- **Wiring:** an add-on run with `dotnet new` instead of `aspire-addon`, or a refusal worked around
  by hand, skips the CLI's consistency checks (`--resource`, `-n`, event store).
- **Secrets:** no connection strings or keys committed beyond the templates' local defaults.
- **Generated files:** `contracts/api.openapi.json` and the generated TS types come from the
  templates; they must not be hand-edited.

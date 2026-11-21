# 23: A scaffolded app's front door: the style guide stays local, and Home is decided

**Status:** planned (2026-11-20), after 18. **Branch:** `feature/23-scaffold-front-door` here, plus a branch and PR in
kamabery/template-projects (Kurt merges). **Depends on:** 18.

## Why

Cloud J-003 (kamabery/booklist, merged as PR #2) deployed a working reading list, but the live preview still showed
the template's **Style guide** in the nav and the template's starter **Home** page ("React + TanStack starter", the
stack cards, a health count). Neither belongs in the app the human asked for. Kurt (2026-11-20): style guides should
show **only locally**, and we need to **find out where the home page came from**.

What is known so far:
- `src/ui/react/src/routes/styleguide.tsx` ships in production builds **on purpose**. Its comment says it is
  the documentation for a starter project, and gating it would make it drift from the Blazor guide at the same path.
  `__root.tsx` puts **Style guide** in the nav unconditionally. Only the router and query devtools are gated on
  `import.meta.env.DEV`.
- `routes/index.tsx` is the template's starter page, never replaced. J-003's spec listed "the Home and Style guide
  pages" under **Scope: Out**. The brief said "just me, on one page", and the spec-writer took that to mean Books
  alone, so no spec decided what the app's front door should be.

## Scope

1. **Style guide only locally** (template-projects, both front ends):
   - React: the `/styleguide` route and its nav item render only when `import.meta.env.DEV`. A production build has
     neither, and the route 404s.
   - Blazor: the same, gated on the host environment (`Development`), so the two guides still match (the reason the
     comment gave for not gating).
   - The template's own docs (`docs/DESIGN.md`, README) say the guide is a local tool. Its tests (Vitest, Playwright,
     bUnit) run against the dev build or assert it's absent from production.
2. **Where Home came from** (investigation first, then a decision):
   - Trace the starter Home through the template (`base-project`, the React and Blazor add-ons, `aspire-starter`
     skills) and the factory (the bootstrap job's scaffold, the spec-writer and grill prompts), and write down which
     piece puts it there and why.
   - Then decide one of: (a) the scaffold's Home becomes a neutral placeholder that names the app and links to its
     resource page; (b) the spec-writer must give Home a decision in every new-app spec (replace it, redirect to the
     resource page, or keep it on purpose); (c) both. Record the decision in the contract ("Ideas", spec-writer) and
     in template-projects.
3. **booklist itself**: a small follow-up job, so its Books page is the home page and the nav has no starter entries.
   It's the first real use of the decided rule.

## Tests

Seen red before green:
- template-projects: a test that a production build of the React add-on has no `/styleguide` route and no nav item
  (red today), and the dev build still has both. The Blazor equivalent. Run the template tooling suite.
- factory (if the decision touches the spec-writer): the spec template and the prompt carry the Home decision; a
  Simulator/Playwright case where a new-app spec names it.
- Live: the next new-repo idea's preview has no Style guide in its nav, and its Home matches what the spec decided.

## Done when

A new scaffold's production build has no style guide. We know, and have written down, where the starter Home comes
from. Every new-app spec decides Home, on the local board and the cloud board. booklist's preview opens on its Books
page. The repo's README offers both boards, local and Azure, and says how to choose (roadmap README, "Both boards at
the end").

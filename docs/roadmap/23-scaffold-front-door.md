# 23: A scaffolded app's front door: the style guide stays local, and Home is decided

**Status:** done (2026-11-22), in review. Template: kamabery/template-projects PR #11 (merged). Factory: the Home rule
in the contract, the grill, the spec template and `submit_specs` / `revise_spec`; ai-factory 0.4.2 on the cloud board.
Follow-ups: booklist on the local board (J-037, booklist PR #3, merged) and pantry on the cloud board (J-006, pantry
PR #3, $1.53, one round, eval good). On the pantry preview, `/` opens Pantry, the nav is Pantry only, and
`/styleguide` is not found. **Branch:** `feature/23-scaffold-front-door`. **Depends on:** 18.

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
- **It happened again on 2026-11-21** (cloud J-005, kamabery/pantry, spec 21's proof). Its preview still opens on
  the starter Home and still shows **Style guide** in the nav. J-005's spec again put "the home page at `/` and
  `/styleguide`" under Scope: Out, so two new apps in two days have shipped a front door nobody asked for. Kurt
  (2026-11-21): "Home is still useless and Styleguide is still showing."

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

## Where Home came from (investigated 2026-11-21)

No one piece is wrong; no step owns the front door.

| Piece | What it does |
| --- | --- |
| template `routes/index.tsx`, `Pages/Home.razor` | The starter Home. `--resource` substitutes Widget/widget only, so the stack blurb survives into every app. |
| template `__root.tsx`, `Layout/NavMenu.razor` | Home, the resource and Style guide in the nav, unconditionally. |
| template `docs/DESIGN.md` §4.1 | Already says Home is a dashboard of every entity, and that the sample Home is "a README that got rendered", the thing §4.1 exists to stop being copied. |
| template `boot-strap-app` Phase 4 | Tells the generated README the style guide "ships in every configuration". |
| factory `templates/bootstrap.md` | Scope Out: anything beyond the scaffold's sample slice, so the bootstrap keeps Home as it is. |
| factory `plugin/skills/grill` | "Name every screen this idea adds or changes": never what the app opens on. |
| factory `plugin/agents/spec-writer.md` | "Spec what [the brief] says and nothing it doesn't", so with no Home in the brief, Home went under Scope: Out (J-003, J-005). |

## Decisions (grilled 2026-11-21)

- **(c) both.** The template's Home becomes a minimal §4.1 dashboard of the sample resource (a count tile and at most
  five recent rows, each linking into its list), with no stack blurb, in React and Blazor. And every new-app idea
  decides Home on purpose.
- **The grill asks, the brief and the spec carry it, and the factory refuses a gap.** For a scaffolded target the grill
  frontier asks what opens at `/`; `brief.md` gains a **Home** section; `spec.md` gains a `- Home:` line (replace,
  redirect to a list, or keep); `submit_specs` refuses a new-app spec set where no spec decides Home, the same shape
  as the Scope-line refusal.
- **booklist and pantry:** `/` redirects to the app's list (`/books`, `/pantry`), the nav shows only the app's own
  pages, and the style guide is dev-only in each app as well. booklist runs on the **local** board, pantry on the
  **cloud** board.

## Tests

Seen red before green:
- template-projects: a test that a production build of the React add-on has no `/styleguide` route and no nav item
  (red today), and the dev build still has both. The Blazor equivalent. Run the template tooling suite.
- factory (if the decision touches the spec-writer): the spec template and the prompt carry the Home decision; a
  Simulator/Playwright case where a new-app spec names it.
- Live: the next new-repo idea's preview has no Style guide in its nav, and its Home matches what the spec decided.
  Met on 2026-11-22 by cloud J-006 (kamabery/pantry), its `- Home: redirect` checked in a browser on the preview.

## Done when

A new scaffold's production build has no style guide. We know, and have written down, where the starter Home comes
from. Every new-app spec decides Home, on the local board and the cloud board. booklist's and pantry's previews open on their own pages
(Books, Pantry). The repo's README offers both boards, local and Azure, and says how to choose (roadmap README, "Both boards at
the end").

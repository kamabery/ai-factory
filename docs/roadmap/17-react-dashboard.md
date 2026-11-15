# 17: React dashboard

**Status:** done (2026-11-15), PR pending. The SPA is `src/factory/Factory.Dashboard` (Vite, React 19, TypeScript, Tailwind 4 on the aspire-ui-react template's tokens, its Button and Spinner, TanStack Query); `npm run build` writes `factory/dashboard/`, committed, so neither install needs Node. The modal stays a div, not the template's native `<dialog>` (it would make the board inert and hide the toast). Playwright 41/41 (smoke 5) on the old page, then on the SPA under the Vite dev server (`FACTORY_DASHBOARD_DEV=1`), then on the build `factory serve` serves; Vitest 79, 99.4% lines (gate 70%); a tool install from a local feed serves it. **Branch:** `feature/17-react-dashboard`. **Depends on:** 12.

## Goal

Rebuild `factory/dashboard/index.html` as a React SPA served by `Factory.Server`, on the
aspire-ui-react template's conventions and design system.

## Scope

Parity first, with no new features:
- the board, with drag to approve, rework, done and discard;
- the job modal (spec edit, files, transcripts, reviews, deploy and preview, QA login, Tear down,
  Retry deploy);
- idea sessions (New idea, reply, brief, specs, Abandon);
- the Lessons and Evaluations tabs, the canary banner, the run-mode chip, Clear.

The API is unchanged; the SPA uses the contract's endpoints.

## Tests

Port `factory/tests/dashboard/dashboard.spec.ts` case for case. It is the oracle. It runs against
the simulator, and passes against the old page first and then the new one. Per `docs/TESTING.md`,
component and hook logic is unit-tested with Vitest and Testing Library (the base of the pyramid);
Playwright keeps the `@smoke` set before every PR (locally) and the whole suite on demand.

## Done when

Every Playwright case passes on the new SPA, and the old `index.html` is deleted.

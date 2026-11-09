# 17: React dashboard

**Status:** not started. **Branch:** `feature/17-react-dashboard`. **Depends on:** 12.

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
Playwright keeps the `@smoke` set on every PR and the whole suite on demand.

## Done when

Every Playwright case passes on the new SPA, and the old `index.html` is deleted.

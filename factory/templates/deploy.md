# Deploy: J-NNN, round N

## Author
- What `aspire-addon infra check` reported, and what was changed in `IaC/` or `.github/workflows/`.
  "Nothing to change" is a valid entry.

## Attempts
- Attempt 1: up / failed - the first real error, and the fix committed (sha).

## Commits after review
- Every commit made in the deploy stage, with its sha. The approver reviews these, because the
  reviewers never saw them. "None" if the first attempt went up.

## Preview
- Front end: <url>
- API: <url>
- Smoke: `/openapi/v1.json` 200, front end loads (or what failed).

## Template gaps
- Symptom / Cause / Fix applied here / Belongs in `template-projects:<path>` - one entry per hand
  edit or diagnosis the templates should have made unnecessary. "None" is a good outcome.

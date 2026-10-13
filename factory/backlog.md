# Sample ideas

Paste one of these into the dashboard's idea box. The spec-writer turns it into one or more backlog
cards for you to edit and approve. The live backlog lives in `.factory-runs/board.json`, never in a
tracked file, so working the backlog never dirties the release checkout. That dirty checkout is
the merge block the source video hit.

## Book search filter
On `/books`, a text box filters the list by name as you type, case-insensitive.
An empty box shows everything. It filters on the client only; there is no API change.

## Author on Book
Add a required `Author` string (max 200) to `Book`, end to end: entity, migration, seeder,
API contract (`contracts/api.openapi.json` regenerated), generated TS types, and a column on
`/books`. The existing API tests are updated, and a new test proves an empty `Author` is rejected
with 400.

## Not-found page
An unknown client route renders a not-found page built from the design system, with a link
back to `/books`. It replaces the router's default.

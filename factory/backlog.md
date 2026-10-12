# Factory backlog

One feature per `##` heading. `/factory next` takes the first heading that has no job on the board
yet (matched by title) and creates a job for it. Ids, stages and history live on the job in
`.factory-runs/board.json`, never here. That means pulling an item never dirties the release
checkout, which is the merge block the source video hit. Edit this file only on `main`, between jobs.

## Book search filter
- On `/books`, a text box filters the list by name as you type, case-insensitive.
  An empty box shows everything. It filters on the client only; there is no API change.

## Author on Book
- Add a required `Author` string (max 200) to `Book`, end to end: entity, migration, seeder,
  API contract (`contracts/api.openapi.json` regenerated), generated TS types, and a column on
  `/books`. The existing API tests are updated, and a new test proves an empty `Author` is rejected
  with 400.

## Not-found page
- An unknown client route renders a not-found page built from the design system, with a link
  back to `/books`. It replaces the router's default.

import { createFileRoute } from '@tanstack/react-router'
import { useLiveQuery } from '@tanstack/react-db'
import { Button } from '@/components/ui/button'
import { Card, CardTitle } from '@/components/ui/card'
import { booksCollection } from '@/db/collections'
import { setDensity, useDensity, type Density } from '@/store/preferences'
import type { Book } from '@/lib/api'

export const Route = createFileRoute('/books')({
  component: BooksPage,
})

const densities: Array<{ value: Density; label: string }> = [
  { value: 'comfortable', label: 'Comfortable' },
  { value: 'compact', label: 'Compact' },
]

const dateFormat = new Intl.DateTimeFormat(undefined, {
  month: 'short',
  day: 'numeric',
  year: 'numeric',
})

/**
 * The API sends a DateOnly ("2026-08-10"). `new Date()` would read that as UTC
 * midnight and render the previous day west of Greenwich, so parse it as local.
 */
function formatDate(isoDate: string): string {
  const [year, month, day] = isoDate.split('-').map(Number)
  return dateFormat.format(new Date(year, month - 1, day))
}

function BooksPage() {
  const density = useDensity()

  // Reads the DB collection, which syncs itself from the API through Query.
  const { data, isLoading, isError } = useLiveQuery((q) =>
    q.from({ book: booksCollection }).orderBy(({ book }) => book.name, 'asc'),
  )

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-3xl font-semibold tracking-tight">Books</h1>
          <p className="mt-1 text-sm text-content-muted">
            GET /api/Books → shelf.api
          </p>
        </div>

        <div className="flex items-center gap-2">
          <div
            className="flex gap-1 rounded-lg border border-border-subtle p-1"
            role="group"
            aria-label="Density"
          >
            {densities.map(({ value, label }) => (
              <Button
                key={value}
                variant={density === value ? 'primary' : 'ghost'}
                onClick={() => setDensity(value)}
                aria-pressed={density === value}
              >
                {label}
              </Button>
            ))}
          </div>
          <Button
            variant="secondary"
            onClick={() => booksCollection.utils.refetch()}
          >
            Refresh
          </Button>
        </div>
      </div>

      {isLoading && <p className="text-content-muted">Loading books…</p>}

      {isError && (
        <Card>
          <CardTitle>Could not reach the API</CardTitle>
          <p className="mt-2 text-sm text-content-muted">
            Start it with <code>dotnet run</code> in{' '}
            <code>src/shelf.api</code>, then hit Refresh. The dev server
            proxies <code>/api</code> to <code>http://localhost:5058</code>.
          </p>
        </Card>
      )}

      {!isLoading && !isError && (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {data.map((book) => (
            <BookCard key={book.id} book={book} density={density} />
          ))}
        </div>
      )}
    </div>
  )
}

function BookCard({
  book,
  density,
}: {
  book: Book
  density: Density
}) {
  return (
    <Card className={density === 'compact' ? 'space-y-0.5 py-3' : 'space-y-1'}>
      <CardTitle>{book.name}</CardTitle>
      <p className="text-3xl font-semibold tabular-nums">{book.quantity}</p>
      {density === 'comfortable' && (
        <p className="text-sm text-content-muted">
          Updated {formatDate(book.updatedOn)}
        </p>
      )}
      {book.isLowStock && (
        // Not colour alone: the badge carries its own text. The fill is the
        // subtle intent token with ordinary content text on it - the solid
        // intent-warning is a light amber and would fail contrast as text.
        <p>
          <span className="inline-block rounded-pill bg-intent-warning-subtle px-2 py-0.5 text-xs font-semibold text-content">
            Low stock
          </span>
        </p>
      )}
    </Card>
  )
}

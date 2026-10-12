import { Link, createFileRoute } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { Card, CardTitle } from '@/components/ui/card'
import { fetchBooks } from '@/lib/api'
import { booksQueryKey } from '@/db/collections'

export const Route = createFileRoute('/')({
  component: HomePage,
})

const stack = [
  ['Router', 'File-based routes in src/routes'],
  ['Query', 'Server cache, shared QueryClient'],
  ['DB', 'Live collections over the query cache'],
  ['Store', 'Client-only UI state'],
]

function HomePage() {
  // Plain TanStack Query — the simplest way to talk to the dotnet API.
  const health = useQuery({
    queryKey: [...booksQueryKey, 'health'],
    queryFn: ({ signal }) => fetchBooks(signal),
    select: (books) => books.length,
  })

  return (
    <div className="space-y-8">
      <section className="space-y-3">
        <h1 className="text-3xl font-semibold tracking-tight">
          React + TanStack starter
        </h1>
        <p className="max-w-2xl text-content-muted">
          Vite, React 19 and Tailwind CSS on the front, the{' '}
          <code className="rounded bg-surface-raised px-1.5 py-0.5 text-sm">
            shelf.api
          </code>{' '}
          dotnet service on the back. Open{' '}
          <Link to="/books" className="text-brand underline">
            Books
          </Link>{' '}
          for the live call.
        </p>
      </section>

      <Card>
        <CardTitle>API status</CardTitle>
        <p className="mt-2 text-lg">
          {health.isPending && (
            <span className="text-content-muted">Checking /api/Books…</span>
          )}
          {health.isError && (
            <span className="text-content-muted">
              Offline — start the API with{' '}
              <code className="rounded bg-surface px-1.5 py-0.5 text-sm">
                dotnet run
              </code>{' '}
              in src/shelf.api.
            </span>
          )}
          {health.isSuccess && (
            <span>
              Online — <strong>{health.data}</strong> books returned.
            </span>
          )}
        </p>
      </Card>

      <section className="grid gap-4 sm:grid-cols-2">
        {stack.map(([name, description]) => (
          <Card key={name}>
            <CardTitle>TanStack {name}</CardTitle>
            <p className="mt-2 text-sm text-content-muted">{description}</p>
          </Card>
        ))}
      </section>
    </div>
  )
}

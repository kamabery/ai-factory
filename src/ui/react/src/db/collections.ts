import { createCollection } from '@tanstack/react-db'
import { queryCollectionOptions } from '@tanstack/query-db-collection'
import { fetchBooks, type Book } from '@/lib/api'
import { queryClient } from '@/lib/query-client'

export const booksQueryKey = ['books'] as const

/**
 * TanStack DB collection backed by TanStack Query. Components read it with
 * `useLiveQuery`, which re-runs incrementally instead of re-filtering on
 * every render.
 */
export const booksCollection = createCollection(
  queryCollectionOptions<Book>({
    id: 'books',
    queryClient,
    queryKey: booksQueryKey,
    queryFn: ({ signal }) => fetchBooks(signal),
    // The primary key from the API. `id` is optional in the generated type
    // because the server assigns it, but every row that has been served has one.
    getKey: (book) => book.id!,
  }),
)

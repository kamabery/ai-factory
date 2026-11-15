import { QueryClient } from '@tanstack/react-query'

/** A poll that fails keeps what it had and tries again on its interval, so no retries. */
export const newQueryClient = () => new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } })

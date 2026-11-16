import { QueryClientProvider, type QueryClient } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { UiProvider } from './ui'

export function Providers({ client, children }: { client: QueryClient; children: ReactNode }) {
  return (
    <QueryClientProvider client={client}>
      <UiProvider>{children}</UiProvider>
    </QueryClientProvider>
  )
}

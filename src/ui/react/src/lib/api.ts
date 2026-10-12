/**
 * Typed client for the dotnet `shelf.api` service.
 *
 * Requests go to `/api/*` and are proxied to the API by Vite in dev
 * (see vite.config.ts), so the browser stays same-origin.
 *
 * The types are not written here — they are generated from
 * `contracts/api.openapi.json`, which the API's own test suite produces. Run
 * `npm run api:types` after changing an endpoint. See docs/ADDING-A-RESOURCE.md.
 */
import type { components } from './api.generated'

/** Generated from the API's OpenAPI document — do not hand-edit the shape. */
export type Book = components['schemas']['Book']

const API_BASE = '/api'

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    signal,
    headers: { Accept: 'application/json' },
  })

  if (!response.ok) {
    throw new Error(
      `GET ${path} failed: ${response.status} ${response.statusText}`,
    )
  }

  return (await response.json()) as T
}

export function fetchBooks(signal?: AbortSignal): Promise<Array<Book>> {
  return getJson<Array<Book>>('/Books', signal)
}

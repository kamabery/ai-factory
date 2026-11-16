// A fake factory server for component tests: `fetch` answers from a route table, and every request is recorded.
import { render, waitFor } from '@testing-library/react'
import { vi } from 'vitest'
import { App } from '@/App'
import { Providers } from '@/app/Providers'
import { newQueryClient } from '@/app/query'
import type { Board, Job } from '@/lib/types'

export type Call = { method: string; url: string; body?: string }
type Reply = string | object | null | undefined
export type Routes = Record<string, Reply | ((call: Call) => Reply | Promise<Reply>)>

export const job = (id: string, stage: string, extra: Partial<Job> = {}): Job => ({
  id, stage, round: 1, title: `job ${id}`, target: 'sim/sandbox',
  createdAt: '2026-11-01T12:00:00Z', approvedAt: '2026-11-01T12:00:00Z', updatedAt: '2026-11-01T12:01:00Z', history: [], ...extra,
})

export const config = { enabled: true, model: 'claude-opus-5-5', owner: 'sim', templatesRepo: 'sim/templates', deploys: true, mode: 'local', provider: 'subscription' }

/** Routes are `METHOD /path` (no query string); a missing GET answers 404, a missing write `{ ok: true, message: 'ok' }`. */
export function fakeServer(routes: Routes, board: Board = { jobs: [], ideas: [] }) {
  const calls: Call[] = []
  const table: Routes = {
    'GET /api/ideate/config': config,
    'GET /state/board.json': board,
    'GET /state/events.jsonl': '',
    'GET /api/sessions': [],
    ...routes,
  }
  vi.stubGlobal('fetch', vi.fn(async (input: string, init?: RequestInit) => {
    const call = { method: init?.method ?? 'GET', url: String(input), body: init?.body as string | undefined }
    calls.push(call)
    let r = table[`${call.method} ${call.url.split('?')[0]}`]
    if (typeof r === 'function') r = await r(call)
    if (r === undefined) return call.method === 'GET' ? new Response('', { status: 404 }) : Response.json({ ok: true, message: 'ok' })
    if (typeof r === 'string') return new Response(r)
    return Response.json(r)
  }))
  return calls
}

export const writes = (calls: Call[]) => calls.filter((c) => c.method !== 'GET').map((c) => ({ method: c.method, url: c.url, body: c.body && tryJson(c.body) }))
const tryJson = (s: string) => { try { return JSON.parse(s) } catch { return s } }

export function renderApp() {
  const r = render(<Providers client={newQueryClient()}><App /></Providers>)
  return { ...r, $: (sel: string) => r.container.ownerDocument.querySelector<HTMLElement>(sel), $$: (sel: string) => [...r.container.ownerDocument.querySelectorAll<HTMLElement>(sel)] }
}

/** Waits for a selector to match, and returns it. */
export async function find(sel: string): Promise<HTMLElement> {
  let el: HTMLElement | null = null
  await waitFor(() => { el = document.querySelector<HTMLElement>(sel); if (!el) throw new Error(`no ${sel}`) })
  return el!
}

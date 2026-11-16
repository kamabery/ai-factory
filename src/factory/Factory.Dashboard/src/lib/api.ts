import type { Result } from './types'

/** A state file or read-only endpoint as text: '' when it is missing. A network error throws, so a poll keeps what it had. */
export async function getText(url: string): Promise<string> {
  const r = await fetch(url, { cache: 'no-store' })
  return r.ok ? r.text() : ''
}

/** JSON from a GET, or null when it is missing or not JSON. */
export async function getJson<T>(url: string): Promise<T | null> {
  const t = await getText(url)
  try { return t ? (JSON.parse(t) as T) : null } catch { return null }
}

/** A write: every one answers { ok, message } (contract, "Dashboard API"). */
export async function api(method: string, url: string, body?: unknown, type = 'application/json'): Promise<Result> {
  try {
    const r = await fetch(url, {
      method,
      headers: { 'Content-Type': type },
      body: body === undefined ? undefined : type === 'application/json' ? JSON.stringify(body) : String(body),
    })
    try { return (await r.json()) as Result } catch { return { ok: false, message: `HTTP ${r.status}` } }
  } catch (err) {
    return { ok: false, message: (err as Error).message }
  }
}

export const enc = encodeURIComponent

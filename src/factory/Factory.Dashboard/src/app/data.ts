// The polls: the board, its events, the canary and the open idea sessions every 1.5 s, the config once. TanStack
// Query's structural sharing keeps an unchanged file's data identical, so nothing re-renders when nothing changed.
import { useQuery } from '@tanstack/react-query'
import { getJson, getText } from '@/lib/api'
import type { Board, Canary, CanaryDue, Config, FactoryEvent, Result, SessionSummary } from '@/lib/types'

export const POLL_MS = 1500
const EMPTY: Board = { jobs: [], ideas: [] }

export const parseEvents = (text: string): FactoryEvent[] =>
  text.split('\n').filter(Boolean).flatMap((l) => { try { return [JSON.parse(l)] } catch { return [] } })

export const keys = { config: ['config'], board: ['board'], events: ['events'], sessions: ['sessions'], canary: ['canary'], canaryDue: ['canary-due'], platform: ['platform'] } as const

/** Whether a canary is due and why: asked only while deploys are on, every 30 s (the server caches it for 60 s). */
export function useCanaryDue() {
  const { config } = useConfig()
  const q = useQuery({
    queryKey: keys.canaryDue,
    queryFn: async () => (await getJson<CanaryDue>('/api/canary')) ?? {},
    refetchInterval: 30_000,
    enabled: !!config.deploys,
  })
  return config.deploys ? (q.data ?? null) : null
}

/** The platform's status word (spec 24), or null: asked only while deploys are on (the server caches it for 30 s). */
export function usePlatform() {
  const { config } = useConfig()
  const q = useQuery({
    queryKey: keys.platform,
    queryFn: async () => { const r = await getJson<Result>('/api/platform'); return r?.ok ? r.message : null },
    refetchInterval: 15_000,
    enabled: !!config.deploys,
  })
  return config.deploys ? (q.data ?? null) : null
}

export function useConfig() {
  const q = useQuery({ queryKey: keys.config, queryFn: async () => (await getJson<Config>('/api/ideate/config')) ?? {}, staleTime: Infinity })
  return { config: q.data ?? { enabled: false }, loaded: q.isSuccess }
}

export function useData() {
  const { config } = useConfig()
  const enabled = !!config.enabled
  const board = useQuery({
    queryKey: keys.board,
    queryFn: async () => { const b = await getJson<Partial<Board>>('/state/board.json'); return { jobs: b?.jobs ?? [], ideas: b?.ideas ?? [] } },
    refetchInterval: POLL_MS,
  })
  const events = useQuery({ queryKey: keys.events, queryFn: async () => parseEvents(await getText('/state/events.jsonl')), refetchInterval: POLL_MS })
  const sessions = useQuery({
    queryKey: keys.sessions,
    queryFn: async () => { const s = await getJson<SessionSummary[]>('/api/sessions'); return Array.isArray(s) ? s : [] },
    refetchInterval: POLL_MS,
    enabled,
  })
  const canary = useQuery({ queryKey: keys.canary, queryFn: () => getJson<Canary>('/state/canary.json'), refetchInterval: POLL_MS })
  return {
    config,
    board: board.data ?? EMPTY,
    /** When the board was last read: the cards' clocks run to it. */
    boardReadAt: board.dataUpdatedAt,
    events: events.data ?? [],
    sessions: enabled ? (sessions.data ?? []) : [],
    canary: canary.data ?? null,
  }
}

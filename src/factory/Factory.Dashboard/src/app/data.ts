// The polls: the board, its events, the canary and the open idea sessions every 1.5 s, the config once. TanStack
// Query's structural sharing keeps an unchanged file's data identical, so nothing re-renders when nothing changed.
import { useQuery } from '@tanstack/react-query'
import { getJson, getText } from '@/lib/api'
import type { Board, Canary, Config, FactoryEvent, SessionSummary } from '@/lib/types'

export const POLL_MS = 1500
const EMPTY: Board = { jobs: [], ideas: [] }

export const parseEvents = (text: string): FactoryEvent[] =>
  text.split('\n').filter(Boolean).flatMap((l) => { try { return [JSON.parse(l)] } catch { return [] } })

export const keys = { config: ['config'], board: ['board'], events: ['events'], sessions: ['sessions'], canary: ['canary'] } as const

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

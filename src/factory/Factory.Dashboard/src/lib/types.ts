// The shapes the dashboard reads: board.json, events.jsonl, canary.json and the API's JSON (contract, "board.json",
// "events.jsonl", "Dashboard API"). Only the fields the page uses.

export type Flag = { reason: string; detail?: string; why?: string; stage?: string; session?: string }

export type HistoryEntry = { ts?: string; from: string; to: string; by: string }

export type Deploy = {
  status?: string
  attempts?: number
  urls?: { frontend?: string | null; api?: string | null } | null
  destroyAt?: string
  destroyError?: string
  destroyFailures?: number
}

export type Merged = { sha: string; repo: string; pr?: string; added?: string[]; modified?: string[] }

export type Job = {
  id: string
  stage: string
  round: number
  title: string
  target?: string
  idea?: string
  kind?: string
  base?: string
  baseMoved?: { by?: string; sha?: string; at?: string } | null
  createdAt?: string
  approvedAt?: string
  updatedAt?: string
  history?: HistoryEntry[]
  flag?: Flag | null
  reviews?: Record<string, string>
  costUsd?: number | null
  durationMs?: number
  pr?: { number: number; url: string } | null
  merged?: Merged | null
  gate?: Record<string, string> | null
  deploy?: Deploy | null
}

export type Idea = {
  id: string
  text: string
  target?: string
  status: string
  jobs?: string[]
  reason?: string
  base?: string
}

export type Board = { jobs: Job[]; ideas: Idea[] }

export type FactoryEvent = {
  ts?: string
  kind: string
  job?: string
  idea?: string
  agent?: string
  agentId?: string
  round?: number
  from?: string
  to?: string
  by?: string
  reason?: string
  detail?: string
  costUsd?: number
  durationMs?: number
}

export type Config = {
  enabled?: boolean
  model?: string
  owner?: string
  templatesRepo?: string
  deploys?: boolean
  mode?: string | null
  provider?: string | null
}

/** GET /api/canary (contract, "Canary", "Why it is due"): the due chip. */
export type CanaryDue = { due?: boolean; reasons?: string[]; typicalMinutes?: number }

export type Canary = {
  status?: string
  error?: string | null
  log?: string
  startedAt?: string
  teardown?: { status?: string; error?: string; failures?: number } | null
}

/** A session's phase, worked out by the server (contract, "Idea sessions (dashboard)", "The phase"). */
export type Phase = {
  name: 'brief' | 'scaffold' | 'specs' | 'build'
  job?: string
  stage?: string
  pr?: { number: number; url: string } | null
  jobs?: { id: string; title?: string; stage: string; pr?: { number: number; url: string } | null; deploy?: Deploy | null }[]
}

export type SessionSummary = {
  id: string
  target?: string
  text: string
  status: string
  idea?: string | null
  bootstrap?: string | null
  costUsd?: number
  newTarget?: boolean
  notices?: Partial<Record<'scaffold-ready' | 'specs-ready', string>> | null
  phase?: Phase
}

/** Every write's answer (contract, "Dashboard API"). */
export type Result = { ok: boolean; message: string }

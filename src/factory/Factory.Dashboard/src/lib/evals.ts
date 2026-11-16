// The Evaluations and Lessons tabs (contract, "Evaluations", "Lessons").
import type { Job } from './types'

export type EvalEntry = {
  job: string; title?: string; idea?: string; target?: string; rating: string; pending?: boolean
  human?: { rating: string } | null; evaluatedAt?: string
  stage?: string; round?: number; flag?: string; gate?: string; decision?: string; merged?: string
  costUsd?: number | null; durationMs?: number; gateMs?: number
  models?: Record<string, { costUsd?: number; outputTokens?: number }>
  agents?: { agent: string; round?: number; ms?: number }[]
}

const num = (id: string) => Number(id.replace(/\D/g, ''))

/** Finished jobs on the board with no evaluation yet: purge keeps them until they have one. */
export function pendingEvals(list: EvalEntry[], jobs: Job[]): EvalEntry[] {
  const done = new Set(list.map((e) => e.job))
  return jobs.filter((j) => (j.stage === 'done' || j.stage === 'discarded') && !done.has(j.id))
    .map((j) => ({ job: j.id, idea: j.idea, title: j.title, target: j.target, rating: 'not evaluated', pending: true,
      stage: j.stage, round: j.round, flag: j.flag?.reason, merged: j.merged?.sha, costUsd: j.costUsd, durationMs: j.durationMs }))
}

export const evalRows = (list: EvalEntry[], jobs: Job[]) => [...list, ...pendingEvals(list, jobs)].sort((a, b) => num(b.job) - num(a.job))

export function parseFeedback(text: string): { rating: string; note: string } {
  const m = text.match(/^Rating: (good|mixed|poor)(?:\r?\n){0,2}([\s\S]*)$/)
  return m ? { rating: m[1], note: m[2].trimEnd() } : { rating: '', note: '' }
}

export const feedbackBody = (rating: string, note: string) => `Rating: ${rating}\n\n${note}\n`

/** The selected lessons, as one idea session's first message on the templates repo. */
export const proposeText = (ids: string[], bodies: string[]) =>
  `Template fixes from factory lessons (${ids.join(', ')}). Each says what went wrong in a job and where the fix belongs; make the templates handle it so the next scaffold needs no hand fix.\n\n${bodies.join('\n\n---\n\n')}`

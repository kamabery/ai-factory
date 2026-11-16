// The board's rules, as the old page applied them (contract, "Board columns and stages", "Blocked", "Deploy").
import type { Canary, FactoryEvent, Job } from './types'

export type Column = { key: string; label: string; stages?: string[] }

export const COLUMNS: Column[] = [
  { key: 'ideas', label: 'Ideas' },
  { key: 'backlog', label: 'Backlog', stages: ['backlog'] },
  { key: 'approved', label: 'Approved', stages: ['approved'] },
  { key: 'building', label: 'Building', stages: ['build', 'review', 'deploy', 'approve', 'gate', 'blocked'] },
  { key: 'ready', label: 'Ready for review', stages: ['ready'] },
  { key: 'done', label: 'Done', stages: ['done'] },
]
/** Where a card may be dropped, by its stage: the columns that light up while it is dragged. */
export const DROP_TARGETS: Record<string, string[]> = { backlog: ['approved', 'done'], ready: ['approved', 'done'] }
export const ROLES = ['security', 'code', 'ux', 'ui']
export const NODES: (string | string[])[] = ['spec-writer', 'builder', ['security-reviewer', 'code-reviewer', 'ux-reviewer', 'ui-reviewer'], 'devops', 'approver', 'gate']
/** Stages that are building. Blocked waits in Building but is not building. */
export const BUILDING = ['build', 'review', 'deploy', 'approve', 'gate']

/** A job blocked from deploy can retry it; one blocked from build or review never had a passing round. */
export const blockedFromDeploy = (j: Job) => j.stage === 'blocked' && (j.history ?? []).at(-1)?.from === 'deploy'
export const usageLimited = (j: Job) => j.flag?.reason === 'usage-limit'

export const leaf = (p: string | undefined) => String(p ?? '').split(/[\\/]/).filter(Boolean).pop() ?? ''

export function duration(ms: number | undefined | null): string {
  if (!ms) return ''
  const s = Math.round(ms / 1000)
  return s < 60 ? `${s}s` : s < 3600 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`
}

/** The card's clock: from approval (or creation) to now, or to its last update once it stopped. */
export function elapsed(j: Job, now: number): string {
  const start = Date.parse(j.approvedAt ?? j.createdAt ?? '')
  const end = ['done', 'discarded', 'ready', 'blocked', 'backlog'].includes(j.stage) ? Date.parse(j.updatedAt ?? '') : now
  if (!start || !end) return ''
  const s = Math.max(0, Math.round((end - start) / 1000))
  return s < 60 ? `${s}s` : duration(s * 1000)
}

const queueOrder = (a: Job, b: Job) => String(a.approvedAt ?? '').localeCompare(String(b.approvedAt ?? '')) || a.id.localeCompare(b.id)

/** What an approved job waits on, by the rules `next` applies. */
export function waitReason(j: Job, jobs: Job[], canary: Canary | null, deploys: boolean | undefined): string {
  const building = jobs.find((x) => BUILDING.includes(x.stage))
  if (building) return `waiting: ${building.id} is building`
  if (canary?.status === 'running') return 'waiting: deploy canary'
  const held = (x: Job) => canary?.status === 'failed' && !!deploys && x.kind !== 'bootstrap'
  if (held(j)) return 'held: deploy canary failed'
  const limited = jobs.find(usageLimited)
  if (limited) return `held: usage limit on ${limited.id}`
  const ahead = jobs.filter((x) => x.stage === 'approved' && !held(x) && queueOrder(x, j) < 0).sort(queueOrder).pop()
  return ahead ? `after ${ahead.id}` : 'next'
}

export type Preview = { status: string; label: string; url: string | null; live: boolean }

/** The preview's state (up, or expired once its auto-destroy time has passed) and where to open it. */
export function preview(j: Job, now: number): Preview {
  const d = j.deploy ?? {}
  const url = d.urls?.frontend ?? (d.urls?.api ? `${d.urls.api}/openapi/v1.json` : null)
  // A failed destroy outranks the timer: what it left behind is still there, and still billing.
  if (d.destroyError && d.status !== 'destroying' && d.status !== 'down') return { status: 'failed', label: 'destroy failed', url, live: false }
  const expired = d.status === 'up' && !!d.destroyAt && Date.parse(d.destroyAt) < now
  const status = expired ? 'expired' : String(d.status)
  return { status, label: status, url, live: status === 'up' }
}

/** Agents between their start and stop, by node, with the ids they run for; a job in gate lights the gate. */
export function activeAgents(events: FactoryEvent[], jobs: Job[]): Record<string, string[]> {
  const running = new Map<string, { id: string; agent: string }>()
  for (const ev of events) {
    if (ev.kind !== 'agent-start' && ev.kind !== 'agent-stop') continue
    const id = ev.job ?? ev.idea ?? ''
    const key = `${id}|${ev.agent}`
    if (ev.kind === 'agent-start') running.set(key, { id, agent: String(ev.agent) })
    else running.delete(key)
  }
  // An escalated or killed run never logs agent-stop, so a job's agents count only while it is on the board and
  // building (a purged job is gone from the board but not from the events).
  const stage = new Map(jobs.map((j) => [j.id, j.stage]))
  const byAgent: Record<string, string[]> = {}
  const add = (agent: string, id: string) => { if (!(byAgent[agent] ??= []).includes(id)) byAgent[agent].push(id) }
  for (const { id, agent } of running.values()) {
    if (/^J-/.test(id) && !BUILDING.includes(stage.get(id) ?? '')) continue
    add(agent, id)
  }
  for (const j of jobs) if (j.stage === 'gate') add('gate', j.id)
  return byAgent
}

/** A card dropped on a column: nothing on its own column, the rework note from Ready to Approved, else a move. */
export function dropAction(stage: string, to: string): 'none' | 'rework' | 'move' {
  if (COLUMNS.find((c) => c.key === to)?.stages?.includes(stage)) return 'none'
  if (stage === 'ready' && to === 'approved') return 'rework'
  return 'move'
}

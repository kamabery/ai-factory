import { describe, expect, it } from 'vitest'
import {
  activeAgents, blockedFromDeploy, dropAction, duration, elapsed, leaf, preview, usageLimited, waitReason,
} from './board'
import type { Canary, FactoryEvent, Job } from './types'

const at = (m: number) => new Date(Date.UTC(2026, 9, 26, 12, m)).toISOString()
const job = (id: string, stage: string, m = 0, extra: Partial<Job> = {}): Job =>
  ({ id, stage, round: 1, title: `job ${id}`, createdAt: at(m), approvedAt: at(m), updatedAt: at(m), history: [], ...extra })

describe('blocked and usage-limited cards', () => {
  it('a job blocked from deploy can retry it; one blocked from review cannot', () => {
    expect(blockedFromDeploy(job('J-1', 'blocked', 0, { history: [{ from: 'deploy', to: 'blocked', by: 'orchestrator' }] }))).toBe(true)
    expect(blockedFromDeploy(job('J-2', 'blocked', 0, { history: [{ from: 'review', to: 'blocked', by: 'orchestrator' }] }))).toBe(false)
    expect(blockedFromDeploy(job('J-3', 'ready', 0, { history: [{ from: 'deploy', to: 'ready', by: 'orchestrator' }] }))).toBe(false)
  })

  it('a usage-limit flag is what Resume keys on', () => {
    expect(usageLimited(job('J-1', 'ready', 0, { flag: { reason: 'usage-limit' } }))).toBe(true)
    expect(usageLimited(job('J-2', 'ready', 0, { flag: { reason: 'max-rounds' } }))).toBe(false)
    expect(usageLimited(job('J-3', 'ready'))).toBe(false)
  })
})

describe('durations', () => {
  it('reads seconds, minutes and hours as the cards show them', () => {
    expect(duration(0)).toBe('')
    expect(duration(4_400)).toBe('4s')
    expect(duration(95_000)).toBe('1m 35s')
    expect(duration(3_725_000)).toBe('1h 2m')
  })

  it('a finished card stops its clock at updatedAt; a building one runs to now', () => {
    expect(elapsed(job('J-1', 'done', 0, { updatedAt: at(2) }), Date.parse(at(30)))).toBe('2m 0s')
    expect(elapsed(job('J-2', 'build', 0), Date.parse(at(1)))).toBe('1m 0s')
    expect(elapsed(job('J-3', 'backlog', 0, { approvedAt: undefined, createdAt: undefined }), 0)).toBe('')
  })

  it('leaf is the last segment of either slash', () => {
    expect(leaf('sim/sandbox')).toBe('sandbox')
    expect(leaf('D:\\code\\todo')).toBe('todo')
    expect(leaf(undefined)).toBe('')
  })
})

describe('what an Approved card waits on', () => {
  const canary = (status: string): Canary => ({ status })

  it('a building job holds the queue, and a blocked one does not', () => {
    const jobs = [job('J-200', 'build'), job('J-201', 'approved', 1), job('J-199', 'blocked')]
    expect(waitReason(jobs[1], jobs, null, true)).toBe('waiting: J-200 is building')
    expect(waitReason(jobs[1], jobs.filter((x) => x.id !== 'J-200'), null, true)).toBe('next')
  })

  it('a running canary holds every job; a failed one holds deployable jobs only while deploys are on', () => {
    const jobs = [job('J-201', 'approved', 1), job('J-202', 'approved', 2), job('J-203', 'approved', 3, { kind: 'bootstrap' })]
    expect(waitReason(jobs[0], jobs, canary('running'), true)).toBe('waiting: deploy canary')
    expect(waitReason(jobs[0], jobs, canary('failed'), true)).toBe('held: deploy canary failed')
    expect(waitReason(jobs[2], jobs, canary('failed'), true)).toBe('next')
    expect(waitReason(jobs[1], jobs, canary('failed'), false)).toBe('after J-201')
  })

  it('a usage limit anywhere holds the queue', () => {
    const jobs = [job('J-340', 'blocked', 1, { flag: { reason: 'usage-limit' } }), job('J-342', 'approved', 3)]
    expect(waitReason(jobs[1], jobs, null, true)).toBe('held: usage limit on J-340')
  })

  it('the queue is approvedAt order, then id', () => {
    const jobs = [job('J-202', 'approved', 2), job('J-201', 'approved', 1), job('J-203', 'approved', 2)]
    expect(waitReason(jobs[1], jobs, canary('pass'), true)).toBe('next')
    expect(waitReason(jobs[0], jobs, canary('pass'), true)).toBe('after J-201')
    expect(waitReason(jobs[2], jobs, canary('pass'), true)).toBe('after J-202')
  })
})

describe('the preview chip', () => {
  const now = Date.parse(at(10))
  const withDeploy = (deploy: Job['deploy']) => job('J-8', 'ready', 0, { deploy })

  it('up with its frontend url is live; with only an api it links the OpenAPI document', () => {
    expect(preview(withDeploy({ status: 'up', urls: { frontend: 'https://fe' } }), now)).toEqual({ status: 'up', label: 'up', url: 'https://fe', live: true })
    expect(preview(withDeploy({ status: 'up', urls: { frontend: null, api: 'https://api' } }), now).url).toBe('https://api/openapi/v1.json')
    expect(preview(withDeploy({ status: 'up', urls: null }), now).url).toBeNull()
  })

  it('past its auto-destroy time it is expired, not live', () => {
    expect(preview(withDeploy({ status: 'up', destroyAt: at(5) }), now)).toMatchObject({ status: 'expired', live: false })
    expect(preview(withDeploy({ status: 'up', destroyAt: at(50) }), now)).toMatchObject({ status: 'up', live: true })
  })

  it('a failed destroy outranks the timer, until a new destroy runs or the preview is down', () => {
    expect(preview(withDeploy({ status: 'up', destroyAt: at(5), destroyError: 'boom' }), now)).toMatchObject({ status: 'failed', label: 'destroy failed', live: false })
    expect(preview(withDeploy({ status: 'destroying', destroyError: 'boom' }), now).status).toBe('destroying')
    expect(preview(withDeploy({ status: 'down', destroyError: 'boom' }), now).status).toBe('down')
  })
})

describe('the pipeline strip', () => {
  const ev = (kind: string, id: string, agent: string): FactoryEvent => (id.startsWith('J-') ? { kind, job: id, agent } : { kind, idea: id, agent })

  it('an agent counts from its start to its stop', () => {
    const jobs = [job('J-1', 'build')]
    expect(activeAgents([ev('agent-start', 'J-1', 'builder')], jobs)).toEqual({ builder: ['J-1'] })
    expect(activeAgents([ev('agent-start', 'J-1', 'builder'), ev('agent-stop', 'J-1', 'builder')], jobs)).toEqual({})
  })

  it('a job that is no longer building, or no longer on the board, has no active agents', () => {
    const events = [ev('agent-start', 'J-2', 'builder'), ev('agent-start', 'J-913', 'builder')]
    expect(activeAgents(events, [job('J-2', 'ready')])).toEqual({})
  })

  it('an idea session agent counts without a job, and a job in gate lights the gate', () => {
    expect(activeAgents([ev('agent-start', 'I-1', 'spec-writer')], [job('J-3', 'gate')])).toEqual({ 'spec-writer': ['I-1'], gate: ['J-3'] })
    expect(activeAgents([{ kind: 'stage', job: 'J-3' }], [])).toEqual({})
  })
})

describe('a drop on a column', () => {
  it('is nothing on its own column, a rework note from Ready to Approved, else a move', () => {
    expect(dropAction('ready', 'ready')).toBe('none')
    expect(dropAction('blocked', 'building')).toBe('none')
    expect(dropAction('ready', 'approved')).toBe('rework')
    expect(dropAction('ready', 'done')).toBe('move')
  })
})

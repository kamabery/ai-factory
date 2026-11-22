import { fireEvent, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeServer, find, job, renderApp, writes } from '@/test/server'

afterEach(() => vi.unstubAllGlobals())

describe('the Canary column', () => {
  it('sits between Approved and Building, and its card follows the running canary', async () => {
    fakeServer({
      'GET /state/canary.json': { status: 'running', startedAt: new Date().toISOString(), phase: 'deploy', deploy: { done: 0, total: 3 } },
      'GET /api/canary': { due: false, reasons: [], typicalMinutes: 12 },
    }, { jobs: [job('J-1', 'canary')], ideas: [] })
    const r = renderApp()
    const card = await find('[data-column="canary"] [data-card="J-1"]')
    expect(r.$$('[data-column]').map((c) => c.dataset.column)).toEqual(['ideas', 'backlog', 'approved', 'canary', 'building', 'ready', 'done'])
    await waitFor(() => expect(card.querySelector('[data-canary-wait]')).toHaveTextContent('canary running · deploying 1/3 · 0 of ~12 min'))
    expect(card.querySelector('[data-canary-failed]')).toBeNull()
    expect(r.$('#canary [data-canary-waiting]')).toHaveTextContent('waiting in Canary: J-1')
  })

  it('after a failure its card is red with the error, Log, Run canary and Discard', async () => {
    const calls = fakeServer({
      'GET /state/canary.json': { status: 'failed', error: 'az missing', log: 'canary-4.log' },
      'POST /api/canary': { ok: true, message: 'canary started' },
    }, { jobs: [job('J-1', 'canary')], ideas: [] })
    const r = renderApp()
    const failed = await find('[data-card="J-1"] [data-canary-failed]')
    expect(failed).toHaveTextContent('az missing')
    expect(r.$('[data-card="J-1"]')).toHaveAttribute('data-red', '1')
    expect(r.$('[data-card="J-1"] a[data-canary-log]')).toHaveAttribute('href', '/state/canary-4.log')
    fireEvent.click(r.$('[data-card="J-1"] [data-run-canary]')!)
    await waitFor(() => expect(writes(calls)).toContainEqual({ method: 'POST', url: '/api/canary', body: {} }))
    fireEvent.click(r.$('[data-card="J-1"] [data-discard]')!)
    await waitFor(() => expect(writes(calls)).toContainEqual({ method: 'POST', url: '/api/jobs/J-1/move', body: { to: 'discarded' } }))
  })

  it('the due chip says jobs wait in Canary', async () => {
    fakeServer({ 'GET /api/canary': { due: true, reasons: ['no pass on record'], typicalMinutes: 15 } })
    renderApp()
    expect(await find('#canary-due')).toHaveTextContent('the next deployable job waits in Canary for it (~15 min)')
  })
})

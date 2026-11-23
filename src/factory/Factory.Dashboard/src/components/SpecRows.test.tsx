import { fireEvent, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeServer, find, job, renderApp, writes } from '@/test/server'

afterEach(() => vi.unstubAllGlobals())

// Spec 22: no Backlog column. A backlog spec is approved from its idea's card in Ideas.
describe('specs in backlog', () => {
  it('the board has no Backlog column, and an idea card lists its backlog specs with Approve and Edit', async () => {
    const calls = fakeServer({ 'POST /api/jobs/J-1/move': { ok: true, message: 'ok' } }, {
      jobs: [job('J-1', 'backlog', { idea: 'I-1', baseMoved: { by: 'J-9' } }), job('J-2', 'approved', { idea: 'I-1' })],
      ideas: [{ id: 'I-1', text: 'an idea', status: 'drafting', target: 'sim/sandbox', jobs: ['J-1', 'J-2'], closeWhenDecided: true }],
    })
    const r = renderApp()
    const row = await find('[data-column="ideas"] [data-idea="I-1"] [data-spec="J-1"]')
    expect(r.$$('[data-column]').map((c) => c.dataset.column)).toEqual(['ideas', 'approved', 'canary', 'building', 'ready', 'done'])
    expect(row).toHaveTextContent('J-1 job J-1')
    expect(row.querySelector('[data-base-moved]')).toHaveTextContent('base moved: J-9 merged')
    expect(r.$('[data-idea="I-1"] [data-spec="J-2"]')).toBeNull()
    expect(r.$('[data-idea="I-1"]')).toHaveTextContent('specs to approve')
    expect(r.$('[data-idea="I-1"]')).not.toHaveTextContent('/factory:idea')

    fireEvent.click(row.querySelector('[data-spec-approve]')!)
    await waitFor(() => expect(writes(calls)).toContainEqual({ method: 'POST', url: '/api/jobs/J-1/move', body: { to: 'approved' } }))
    fireEvent.click(row.querySelector('[data-spec-edit]')!)
    expect(await find('#modal #spec-editor')).toBeInTheDocument()
  })

  it('a session card lists its idea\'s backlog specs', async () => {
    fakeServer({
      'GET /api/sessions': [{ id: 'S-1', idea: 'I-1', text: 'an idea', target: 'sim/sandbox', status: 'idle', phase: { name: 'specs' } }],
    }, { jobs: [job('J-1', 'backlog', { idea: 'I-1' })], ideas: [{ id: 'I-1', text: 'an idea', status: 'drafting', jobs: ['J-1'] }] })
    renderApp()
    expect(await find('[data-session="S-1"] [data-spec="J-1"] [data-spec-approve]')).toHaveTextContent('Approve')
  })

  it('a spec with no idea shown gets its own card with Approve, Edit and Discard', async () => {
    const calls = fakeServer({ 'POST /api/jobs/J-1/move': { ok: true, message: 'ok' } }, {
      jobs: [job('J-1', 'backlog', { idea: 'I-1' }), job('J-2', 'backlog')],
      ideas: [{ id: 'I-1', text: 'closed by the CLI', status: 'done', jobs: ['J-1'] }],
    })
    const r = renderApp()
    const own = await find('[data-column="ideas"] [data-card="J-1"][data-spec-card]')
    expect(r.$('[data-column="ideas"] [data-card="J-2"][data-spec-card]')).toBeInTheDocument()
    expect(own.querySelector('[data-spec-edit]')).toBeInTheDocument()
    fireEvent.click(own.querySelector('[data-discard]')!)
    await waitFor(() => expect(writes(calls)).toContainEqual({ method: 'POST', url: '/api/jobs/J-1/move', body: { to: 'discarded' } }))
  })

  it('a refused approval stays on its row until a try succeeds', async () => {
    let refuse = true
    fakeServer({ 'POST /api/jobs/J-1/move': () => refuse ? { ok: false, message: 'approve refused: the spec has no Gate' } : { ok: true, message: 'ok' } },
      { jobs: [job('J-1', 'backlog', { idea: 'I-1' })], ideas: [{ id: 'I-1', text: 'an idea', status: 'drafting', jobs: ['J-1'] }] })
    const r = renderApp()
    const approve = await find('[data-spec="J-1"] [data-spec-approve]')
    fireEvent.click(approve)
    expect(await find('[data-spec="J-1"] [data-refused]')).toHaveTextContent('approve refused: the spec has no Gate')
    refuse = false
    fireEvent.click(r.$('[data-spec="J-1"] [data-spec-approve]')!)
    await waitFor(() => expect(r.$('[data-spec="J-1"] [data-refused]')).toBeNull())
  })
})

import { fireEvent, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { config, fakeServer, find, job, renderApp, writes } from '@/test/server'

afterEach(() => vi.unstubAllGlobals())

const evals = [
  { job: 'J-1', title: 'Book search', rating: 'good', stage: 'done', round: 1, gate: 'green', decision: 'APPROVE', merged: '8729d06aa', costUsd: 0.42, durationMs: 95000, gateMs: 2000,
    evaluatedAt: '2026-11-15T10:00:00Z', models: { 'claude-opus-5-5': { costUsd: 0.38, outputTokens: 4200 }, 'claude-sonnet-5-5': { costUsd: 0.04 } },
    agents: [{ agent: 'builder', round: 1, ms: 1000 }, { agent: 'approver', round: 1 }] },
  { job: 'J-2', title: 'Sort', rating: 'mixed', stage: 'ready', round: 3, flag: 'max-rounds', human: { rating: 'poor' } },
]

describe('the Evaluations tab', () => {
  it('lists every evaluation and every finished job not yet evaluated, newest first, and opens one', async () => {
    fakeServer({
      'GET /api/evals': evals,
      'GET /state/evals/J-1.md': '# Eval: J-1',
      'GET /state/evals/J-1.feedback.md': '',
    }, { jobs: [job('J-3', 'discarded'), job('J-4', 'backlog')], ideas: [] })
    const { $, $$ } = renderApp()
    fireEvent.click(await find('[data-view="evals"]'))
    expect($('#board')).toBeNull()
    await waitFor(() => expect($$('[data-eval]').map((e) => e.dataset.eval)).toEqual(['J-3', 'J-2', 'J-1']))
    expect($('[data-eval="J-3"][data-pending] [data-rating]')).toHaveTextContent('not evaluated')
    expect($('[data-eval="J-2"] [data-human-rating]')).toHaveTextContent('you: poor')
    expect($('[data-eval="J-2"]')).toHaveTextContent('max-rounds')
    expect($('[data-eval-body]')).toHaveTextContent('Pick an evaluation.')
    fireEvent.click($('[data-eval="J-1"]')!)
    await waitFor(() => expect($('[data-eval-body] h1')).toHaveTextContent('Eval: J-1'))
    expect($('[data-eval-body] > :first-child')).toHaveAttribute('data-eval-facts', '')
    expect($('[data-fact="gate"]')).toHaveTextContent('green')
    expect($('[data-fact="time"]')).toHaveTextContent('1m 35s')
    expect($('[data-model="claude-opus-5-5"]')).toHaveTextContent('$0.38 · 4,200 out')
    expect($('[data-agent="approver"]')).toHaveTextContent('0s')
    fireEvent.click($('[data-eval="J-3"]')!)
    expect($('[data-eval-body]')).toHaveTextContent('factory eval J-3 -File')
    expect($('[data-feedback]')).toBeNull()
    fireEvent.click($('[data-view="board"]')!)
    await find('#board')
  })

  it('with none says how a job is evaluated, and facts without logs say so', async () => {
    fakeServer({ 'GET /api/evals': [] })
    const { $ } = renderApp()
    fireEvent.click(await find('[data-view="evals"]'))
    await waitFor(() => expect($('#evals')).toHaveTextContent('No evaluations yet'))
  })

  it('the human rates and notes an evaluation; the draft survives polls; Save writes it', async () => {
    const calls = fakeServer({
      'GET /api/evals': [{ ...evals[1], agents: [], models: { m: {} } }],
      'GET /state/evals/J-2.md': '# Eval: J-2',
      'GET /state/evals/J-2.feedback.md': 'Rating: mixed\n\nearlier note\n',
      'PUT /api/evals/J-2/feedback': { ok: true, message: 'feedback saved' },
    })
    const { $ } = renderApp()
    fireEvent.click(await find('[data-view="evals"]'))
    fireEvent.click(await find('[data-eval="J-2"]'))
    await waitFor(() => expect($('[data-feedback] textarea')).toHaveValue('earlier note'))
    expect($('[data-human="mixed"]')).toHaveClass('on')
    expect($('[data-fact="rounds"]')).toHaveTextContent('flag max-rounds')
    expect($('[data-eval-facts]')).toHaveTextContent('no agent events')
    fireEvent.click($('[data-human="poor"]')!)
    fireEvent.change($('[data-feedback] textarea')!, { target: { value: 'split it' } })
    fireEvent.click($('[data-save-feedback]')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('feedback saved'))
    expect(writes(calls)).toEqual([{ method: 'PUT', url: '/api/evals/J-2/feedback', body: 'Rating: poor\n\nsplit it\n' }])
  })

  it('Save without a rating asks for one', async () => {
    fakeServer({ 'GET /api/evals': [evals[0]], 'GET /state/evals/J-1.feedback.md': '' })
    const { $ } = renderApp()
    fireEvent.click(await find('[data-view="evals"]'))
    fireEvent.click(await find('[data-eval="J-1"]'))
    fireEvent.click(await find('[data-save-feedback]'))
    expect($('#toast')).toHaveTextContent('Pick a rating first')
  })
})

describe('the Lessons tab', () => {
  const lessons = [
    { job: 'J-8', title: 'Preview', status: 'open', templateGaps: 1, at: '2026-11-15T10:00:00Z', target: 'sim/sandbox' },
    { job: 'J-7', title: 'Keycloak', status: 'proposed', session: 'S-3', flag: 'escalated', templateGaps: 2 },
    { job: 'J-2', title: 'Sort', status: 'open' },
  ]

  it('lists the lessons, opens one, and marks a proposed one applied', async () => {
    const calls = fakeServer({ 'GET /api/lessons': lessons, 'GET /state/lessons/J-8.md': 'add an empty-state' })
    const { $ } = renderApp()
    fireEvent.click(await find('#views [data-view="lessons"]'))
    await find('[data-lesson="J-8"]')
    expect($('[data-lesson="J-8"] [data-gaps]')).toHaveTextContent('1 template gap')
    expect($('[data-lesson="J-7"] [data-gaps]')).toHaveTextContent('2 template gaps')
    expect($('[data-lesson="J-2"] [data-gaps]')).toHaveTextContent('no template gaps')
    expect($('[data-lesson="J-7"] [data-status]')).toHaveTextContent('proposed S-3')
    expect($('[data-lesson="J-7"] [data-select]')).toBeNull()
    expect($('[data-propose]')).toBeDisabled()
    fireEvent.click($('[data-lesson="J-8"] .title')!)
    await waitFor(() => expect($('[data-lesson-body]')).toHaveTextContent('empty-state'))
    fireEvent.click($('[data-lesson="J-7"] [data-applied]')!)
    await waitFor(() => expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/lessons/J-7/mark', body: { status: 'applied' } }]))
  })

  it('Propose template fix starts one session with the selected lessons and marks them proposed', async () => {
    const calls = fakeServer({
      'GET /api/lessons': lessons,
      'GET /state/lessons/J-8.md': 'empty-state',
      'GET /state/lessons/J-2.md': 'sort',
      'POST /api/sessions': { ok: true, message: 'S-9' },
    })
    const { $ } = renderApp()
    fireEvent.click(await find('#views [data-view="lessons"]'))
    fireEvent.click(await find('[data-lesson="J-8"] [data-select]'))
    fireEvent.click($('[data-lesson="J-2"] [data-select]')!)
    fireEvent.click($('[data-lesson="J-2"] [data-select]')!)
    fireEvent.click($('[data-lesson="J-2"] [data-select]')!)
    expect($('[data-lesson-body]')).toHaveTextContent('Pick a lesson.')
    fireEvent.click($('[data-propose]')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('Proposed as S-9 on sim/templates'))
    const w = writes(calls)
    expect(w[0].url).toBe('/api/sessions')
    expect(w[0].body.target).toBe('sim/templates')
    expect(w[0].body.text).toContain('empty-state\n\n---\n\nsort')
    expect(w.slice(1)).toEqual([
      { method: 'POST', url: '/api/lessons/J-8/mark', body: { status: 'proposed', session: 'S-9' } },
      { method: 'POST', url: '/api/lessons/J-2/mark', body: { status: 'proposed', session: 'S-9' } },
    ])
  })

  it('without a templates repo, or with idea sessions off, or a refusal, says what to do', async () => {
    fakeServer({ 'GET /api/lessons': lessons, 'GET /api/ideate/config': { ...config, templatesRepo: undefined } })
    const r = renderApp()
    fireEvent.click(await find('#views [data-view="lessons"]'))
    expect(await find('#lessons')).toHaveTextContent('set FACTORY_TEMPLATES_REPO in .env')
    fireEvent.click(await find('[data-lesson="J-8"] [data-select]'))
    fireEvent.click(r.$('[data-propose]')!)
    await waitFor(() => expect(r.$('#toast')).toHaveTextContent('Set FACTORY_TEMPLATES_REPO'))
    r.unmount()

    fakeServer({ 'GET /api/lessons': lessons, 'GET /api/ideate/config': { ...config, enabled: false } })
    const r2 = renderApp()
    fireEvent.click(await find('#views [data-view="lessons"]'))
    fireEvent.click(await find('[data-lesson="J-8"] [data-select]'))
    fireEvent.click(r2.$('[data-propose]')!)
    await waitFor(() => expect(r2.$('#toast')).toHaveTextContent('Idea sessions are off in this run mode'))
    r2.unmount()

    fakeServer({ 'GET /api/lessons': lessons, 'POST /api/sessions': { ok: false, message: 'no clone' }, 'POST /api/lessons/J-8/mark': { ok: false, message: 'x' } })
    const r3 = renderApp()
    fireEvent.click(await find('#views [data-view="lessons"]'))
    fireEvent.click(await find('[data-lesson="J-8"] [data-select]'))
    fireEvent.click(r3.$('[data-propose]')!)
    await waitFor(() => expect(r3.$('#toast')).toHaveTextContent('no clone'))
  })

  it('with none says where lessons come from', async () => {
    fakeServer({ 'GET /api/lessons': [] })
    const { $ } = renderApp()
    fireEvent.click(await find('#views [data-view="lessons"]'))
    await waitFor(() => expect($('#lessons')).toHaveTextContent('No lessons yet'))
  })
})

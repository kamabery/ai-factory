import { act, fireEvent, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { config, fakeServer, find, job, renderApp, writes } from '@/test/server'

beforeEach(() => { vi.stubGlobal('confirm', () => true) })
afterEach(() => { vi.unstubAllGlobals(); document.documentElement.removeAttribute('data-theme') })

describe('the board', () => {
  it('puts every card in its column, with its flags, reviews, PR, cost and what it waits on', async () => {
    fakeServer({}, {
      jobs: [
        job('J-1', 'done', { reviews: { security: 'PASS', code: 'PASS', ux: 'PASS', ui: 'CHANGES' }, costUsd: 0.42 }),
        job('J-2', 'ready', { flag: { reason: 'max-rounds', detail: 'ui' }, pr: { number: 7, url: 'https://gh/7' } }),
        job('J-3', 'approved', { kind: 'bootstrap', base: 'J-2', baseMoved: { by: 'J-1' } }),
        job('J-4', 'deploy', { deploy: { status: 'deploying', attempts: 2 } }),
        job('J-5', 'discarded'),
      ],
      ideas: [{ id: 'I-1', text: 'an idea', status: 'drafting', target: 'sim/sandbox', base: 'J-2' }, { id: 'I-2', text: 'gone', status: 'done' }],
    })
    const { $, $$ } = renderApp()
    await find('[data-column="done"] [data-card="J-1"]')
    expect($('[data-card="J-1"] [data-review="ui"]')).toHaveTextContent('ui:CHANGES')
    expect($('[data-card="J-1"] [data-cost]')).toHaveTextContent('$0.42')
    expect($('[data-column="ready"] [data-card="J-2"] [data-flag]')).toHaveTextContent('max-rounds')
    expect($('[data-card="J-2"] a[data-pr]')).toHaveAttribute('href', 'https://gh/7')
    expect($('[data-card="J-3"] [data-wait]')).toHaveTextContent('waiting: J-4 is building')
    expect($('[data-card="J-3"] [data-base]')).toHaveTextContent('on J-2')
    expect($('[data-card="J-3"] [data-base-moved]')).toHaveTextContent('base moved: J-1 merged')
    expect($('[data-card="J-3"] [data-kind="bootstrap"]')).toBeInTheDocument()
    expect($('[data-card="J-4"] .sub')).toHaveTextContent('deploy r1 · 2/5')
    expect($('[data-card="J-5"]')).toBeNull()
    expect($('[data-idea="I-1"]')).toHaveTextContent('/factory:idea I-1')
    expect($('[data-idea="I-2"]')).toBeNull()
    expect($$('[data-node]').map((n) => n.dataset.node)).toContain('gate')
    expect($('[data-node="devops"]')).not.toHaveClass('active')
  })

  it('lights a pipeline node with the job its agent runs for', async () => {
    fakeServer({ 'GET /state/events.jsonl': '{"kind":"agent-start","job":"J-1","agent":"builder"}\n' }, { jobs: [job('J-1', 'build')], ideas: [] })
    const { $ } = renderApp()
    await waitFor(() => expect($('[data-node="builder"]')).toHaveClass('active'))
    expect($('[data-node="builder"]')).toHaveTextContent('J-1')
  })

  it('names the run mode in the header, loudly in cloud mode, and says when it is invalid', async () => {
    fakeServer({ 'GET /api/ideate/config': { ...config, mode: 'cloud', provider: 'foundry' } })
    const { $ } = renderApp()
    await waitFor(() => expect($('#mode')).toHaveTextContent('cloud · foundry'))
    expect($('#mode')).toHaveAttribute('data-mode', 'cloud')
    expect($('#mode')?.title).toMatch(/Microsoft Foundry/)
  })

  it('says FACTORY_MODE is invalid when the config has no mode', async () => {
    fakeServer({ 'GET /api/ideate/config': { enabled: false, mode: null } })
    const { $ } = renderApp()
    await waitFor(() => expect($('#mode')).toHaveTextContent('FACTORY_MODE is invalid'))
    expect($('#new-idea-btn')).toBeNull()
  })

  it('toggles the theme and remembers it', async () => {
    vi.stubGlobal('matchMedia', () => ({ matches: false }))
    fakeServer({})
    const { $ } = renderApp()
    fireEvent.click($('#theme')!)
    expect(document.documentElement.dataset.theme).toBe('dark')
    fireEvent.click($('#theme')!)
    expect(document.documentElement.dataset.theme).toBe('light')
    expect(localStorage.getItem('factory-theme')).toBe('light')
  })
})

describe('dragging a card', () => {
  const drag = (from: HTMLElement, to: HTMLElement) => {
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' }
    fireEvent.dragStart(from, { dataTransfer })
    fireEvent.dragOver(to, { dataTransfer })
    fireEvent.drop(to, { dataTransfer })
  }

  it('to Approved approves a backlog card, and lights the columns it may land on', async () => {
    const calls = fakeServer({}, { jobs: [job('J-1', 'backlog')], ideas: [] })
    const { $ } = renderApp()
    const card = await find('[data-card="J-1"]')
    expect(card).toHaveAttribute('draggable', 'true')
    fireEvent.dragStart(card, { dataTransfer: { setData: vi.fn() } })
    expect($('[data-column="approved"]')).toHaveClass('drop-ok')
    expect($('[data-column="ready"]')).not.toHaveClass('drop-ok')
    fireEvent.drop($('[data-column="approved"]')!)
    await waitFor(() => expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/jobs/J-1/move', body: { to: 'approved' } }]))
    await waitFor(() => expect($('#toast')).toHaveTextContent('J-1 → Approved'))
  })

  it('a refused move says why; a drop on its own column does nothing', async () => {
    const calls = fakeServer({ 'POST /api/jobs/J-1/move': { ok: false, message: 'merge refused: not ready' } }, { jobs: [job('J-1', 'backlog')], ideas: [] })
    const { $ } = renderApp()
    const card = await find('[data-card="J-1"]')
    drag(card, $('[data-column="backlog"]')!)
    expect(writes(calls)).toEqual([])
    drag(card, $('[data-column="done"]')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('merge refused: not ready'))
    expect($('#toast')).toHaveClass('error')
  })

  it('from Ready to Approved opens the card for its rework note', async () => {
    const calls = fakeServer({}, { jobs: [job('J-1', 'ready')], ideas: [] })
    const { $ } = renderApp()
    drag(await find('[data-card="J-1"]'), $('[data-column="approved"]')!)
    await find('#rework-note')
    expect($('#toast')).toHaveTextContent('Add a rework note, then press Rework')
    expect(writes(calls)).toEqual([])
  })
})

describe('card actions', () => {
  it('a live preview offers Launch, QA login and Tear down', async () => {
    const calls = fakeServer({
      'GET /api/jobs/J-8/qa-login': { ok: true, message: 'user: qa\npassword: pw' },
      'POST /api/jobs/J-8/destroy': { ok: true, message: 'destroying' },
    }, { jobs: [job('J-8', 'ready', { deploy: { status: 'up', urls: { frontend: 'https://fe' }, destroyAt: '2999-01-01T00:00:00Z' } })], ideas: [] })
    const { $ } = renderApp()
    await find('[data-card="J-8"] [data-preview]')
    expect($('[data-preview]')).toHaveTextContent('preview: up')
    expect($('[data-preview]')?.title).toMatch(/auto-destroys/)
    expect($('a[data-launch]')).toHaveAttribute('href', 'https://fe')
    fireEvent.click($('[data-qa]')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('user: qa · password: pw'))
    fireEvent.click($('[data-teardown]')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('J-8: destroying'))
    expect($('#backdrop')).not.toHaveClass('open')
    expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/jobs/J-8/destroy', body: {} }])
  })

  it('a refused QA login says why', async () => {
    fakeServer({ 'GET /api/jobs/J-8/qa-login': 'not json' }, { jobs: [job('J-8', 'ready', { deploy: { status: 'up', urls: null } })], ideas: [] })
    const { $ } = renderApp()
    fireEvent.click(await find('[data-qa]'))
    await waitFor(() => expect($('#toast')).toHaveTextContent('HTTP 200'))
  })

  it('a blocked card offers Retry deploy, Rework and Discard; a usage-limited one Resume', async () => {
    const calls = fakeServer({}, {
      jobs: [
        job('J-1', 'blocked', { flag: { reason: 'deploy-failed' }, history: [{ from: 'deploy', to: 'blocked', by: 'orchestrator' }] }),
        job('J-2', 'blocked', { flag: { reason: 'usage-limit' }, history: [{ from: 'review', to: 'blocked', by: 'runner' }] }),
        job('J-3', 'ready', { flag: { reason: 'usage-limit' } }),
      ], ideas: [],
    })
    const { $ } = renderApp()
    fireEvent.click(await find('[data-card="J-1"] [data-redeploy]'))
    await waitFor(() => expect($('#toast')).toHaveTextContent('J-1: retrying deploy'))
    expect($('[data-card="J-2"] [data-redeploy]')).toBeNull()
    fireEvent.click($('[data-card="J-2"] [data-resume]')!)
    fireEvent.click($('[data-card="J-1"] [data-discard]')!)
    expect($('[data-card="J-3"] [data-resume]')).toBeInTheDocument()
    await waitFor(() => expect(writes(calls).map((c) => c.url)).toEqual(['/api/jobs/J-1/redeploy', '/api/jobs/J-2/resume', '/api/jobs/J-1/move']))
    fireEvent.click($('[data-card="J-1"] [data-rework]')!)
    await find('#rework-note')
  })

  it('a failed destroy is a failed chip with Tear down, even past its timer', async () => {
    fakeServer({}, { jobs: [job('J-8', 'ready', { deploy: { status: 'up', destroyAt: '2020-01-01T00:00:00Z', destroyError: 'boom' } })], ideas: [] })
    const { $ } = renderApp()
    await find('[data-preview]')
    expect($('[data-preview]')).toHaveTextContent('preview: destroy failed')
    expect($('[data-preview]')).toHaveClass('failed')
    expect($('[data-preview]')?.title).toBe('boom')
    expect($('[data-teardown]')).toBeInTheDocument()
  })

  it('Clear purges and says what it kept until evaluated', async () => {
    let jobs = [job('J-1', 'done'), job('J-2', 'done')]
    fakeServer({
      'GET /state/board.json': () => ({ jobs, ideas: [] }),
      'POST /api/purge': () => { jobs = [jobs[1]]; return { ok: true, message: 'J-1' } },
    })
    const { $ } = renderApp()
    fireEvent.click(await find('#purge-btn'))
    await waitFor(() => expect($('#toast')).toHaveTextContent('Cleared J-1 · J-2 kept until evaluated'))
  })

  it('Clear with nothing to clear says so', async () => {
    fakeServer({ 'POST /api/purge': { ok: true, message: '' } })
    const { $ } = renderApp()
    fireEvent.click(await find('#purge-btn'))
    await waitFor(() => expect($('#toast')).toHaveTextContent('Nothing to clear'))
  })
})

describe('Ideas cards', () => {
  it('Abandon asks once, then abandons; a session card the same', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const calls = fakeServer({
      'GET /api/sessions': [{ id: 'S-1', text: 'radar', status: 'idle', target: 'sim/sandbox', costUsd: 0.5, bootstrap: 'J-9' }, { id: 'S-2', text: 'busy', status: 'running' }, { id: 'S-3', text: 'broken', status: 'error', idea: 'I-1' },
        { id: 'S-4', text: 'new repo', status: 'idle', bootstrap: 'J-8', idea: 'I-4', phase: { name: 'scaffold', job: 'J-8', stage: 'review', pr: null } },
        { id: 'S-5', text: 'new repo', status: 'idle', bootstrap: 'J-7', idea: 'I-5', phase: { name: 'scaffold', job: 'J-7', stage: 'ready', pr: null } }],
    }, { jobs: [{ id: 'J-9', title: 'boot', stage: 'build', target: 'sim/x', round: 1 }], ideas: [{ id: 'I-1', text: 'in a session', status: 'drafting' }, { id: 'I-2', text: 'waits', status: 'waiting', jobs: ['J-9'] }, { id: 'I-3', text: 'failed', status: 'failed', reason: 'too vague' }] })
    const { $ } = renderApp()
    const btn = await find('[data-idea="I-2"] [data-abandon]')
    expect($('[data-idea="I-1"]')).toBeNull()   // its session's card stands for it
    expect($('[data-idea="I-2"]')).toHaveTextContent('waiting for bootstrap J-9')
    expect($('[data-idea="I-3"]')).toHaveTextContent('too vague')
    expect($('[data-session="S-1"]')).toHaveTextContent('your turn')
    expect($('[data-session="S-2"]')).toHaveTextContent('Claude is working')
    expect($('[data-session="S-2"] [data-abandon]')).toBeNull()
    expect($('[data-session="S-3"] .flag')).toHaveTextContent('error')
    // Beside Abandon: whose move it is, with a moving marker while the factory has it.
    expect($('[data-idea="I-2"] .meta [data-wait="factory"]')).toHaveTextContent('waiting for bootstrap J-9 (building)')
    expect($('[data-idea="I-2"] .meta [data-wait] [data-spinner]')).not.toBeNull()
    expect($('[data-session="S-1"] [data-wait="you"]')).toHaveTextContent('your turn')
    expect($('[data-session="S-1"] [data-spinner]')).toBeNull()
    expect($('[data-session="S-2"] [data-wait="factory"] [data-spinner]')).not.toBeNull()
    expect($('[data-session="S-4"] [data-wait="factory"]')).toHaveTextContent('waiting for bootstrap J-8 (in review)')
    expect($('[data-session="S-4"] [data-wait] + [data-abandon]')).not.toBeNull()
    expect($('[data-session="S-5"] [data-wait="you"]')).toHaveTextContent('Ready: merge the scaffold')
    fireEvent.click(btn)
    expect(btn).toHaveTextContent('Abandon?')
    await act(async () => { vi.advanceTimersByTime(4100) })
    expect(btn).toHaveTextContent('Abandon')
    fireEvent.click(btn)
    fireEvent.click(btn)
    const s = $('[data-session="S-1"] [data-abandon]')!
    fireEvent.click(s)
    fireEvent.click(s)
    await waitFor(() => expect(writes(calls)).toEqual([
      { method: 'POST', url: '/api/ideas/I-2/abandon', body: {} },
      { method: 'POST', url: '/api/sessions/S-1/close', body: { abandon: true } },
    ]))
    expect($('#backdrop')).not.toHaveClass('open')
    vi.useRealTimers()
  })
})

describe('the canary banner', () => {
  it('a failed canary shows its error and log, and Run canary starts one', async () => {
    const calls = fakeServer({
      'GET /state/canary.json': { status: 'failed', error: 'az missing', log: 'canary-4.log' },
      'POST /api/canary': { ok: true, message: 'canary started' },
    }, { jobs: [job('J-1', 'approved')], ideas: [] })
    const { $ } = renderApp()
    const banner = await find('#canary')
    expect(banner).toHaveTextContent('Deployable jobs are waiting')
    expect($('a[data-canary-log]')).toHaveAttribute('href', '/state/canary-4.log')
    expect($('[data-card="J-1"] [data-wait]')).toHaveTextContent('held: deploy canary failed')
    fireEvent.click($('[data-run-canary]')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('canary started'))
    expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/canary', body: {} }])
  })

  it('a running canary, and a pass whose teardown failed, say so', async () => {
    fakeServer({ 'GET /state/canary.json': { status: 'running', startedAt: '2026-11-01T12:00:00Z' } })
    const { $, unmount } = renderApp()
    await waitFor(() => expect($('#canary')).toHaveTextContent('Deploy canary running'))
    unmount()
    fakeServer({ 'GET /state/canary.json': { status: 'pass', log: 'c.log', teardown: { status: 'failed', error: '404', failures: 2 } } })
    const r = renderApp()
    await waitFor(() => expect(r.$('#canary')).toHaveTextContent('could not be torn down'))
    expect(r.$('#canary')).not.toHaveTextContent('Deployable jobs are waiting')
  })

  it('a pass shows no banner', async () => {
    fakeServer({ 'GET /state/canary.json': { status: 'pass' } }, { jobs: [job('J-1', 'backlog')], ideas: [] })
    const { $ } = renderApp()
    await find('[data-card="J-1"]')
    expect($('#canary')).toBeNull()
  })
})

describe('the canary due chip', () => {
  it('says why a canary is due and how long it takes, and is quiet when none is', async () => {
    fakeServer({ 'GET /api/canary': { due: true, reasons: ['templates changed since pass #32', 'factory 0.4.1 → 0.4.2'], typicalMinutes: 17 } })
    const { unmount } = renderApp()
    const chip = await find('#canary-due')
    expect(chip).toHaveTextContent('canary due: templates changed since pass #32; factory 0.4.1 → 0.4.2')
    expect(chip).toHaveTextContent('the next deployable job waits for it (~17 min)')
    unmount()
    fakeServer({ 'GET /api/canary': { due: false, reasons: [], typicalMinutes: 15 } }, { jobs: [job('J-1', 'backlog')], ideas: [] })
    const r = renderApp()
    await find('[data-card="J-1"]')
    expect(r.$('#canary-due')).toBeNull()
  })

  it('is not asked for while deploys are off', async () => {
    const calls = fakeServer({ 'GET /api/ideate/config': { ...config, deploys: false }, 'GET /api/canary': { due: true, reasons: ['no pass on record'], typicalMinutes: 15 } },
      { jobs: [job('J-1', 'backlog')], ideas: [] })
    const { $ } = renderApp()
    await find('[data-card="J-1"]')
    await new Promise((r) => setTimeout(r, 50))
    expect($('#canary-due')).toBeNull()
    expect(calls.some((c) => c.url === '/api/canary' && c.method === 'GET')).toBe(false)
  })
})

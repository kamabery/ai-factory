import { fireEvent, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeServer, find, job, renderApp, writes, type Routes } from '@/test/server'
import type { Board } from '@/lib/types'

beforeEach(() => vi.stubGlobal('confirm', () => true))
afterEach(() => vi.unstubAllGlobals())

const summary = { id: 'S-1', target: 'sim/sandbox', text: 'Tag books', status: 'idle', idea: null, costUsd: 0.1 }

/** A session whose session.json is `s` (a function of the writes so far, to follow them). */
function session(s: (n: number) => object, extra: Routes = {}, board?: Board) {
  let n = 0
  const calls = fakeServer({
    'GET /api/sessions': [summary],
    'GET /state/sessions/S-1/session.json': () => s(n),
    'GET /state/sessions/S-1/chat.json': { messages: [{ role: 'user', content: 'prompt' }, { role: 'assistant', content: [{ type: 'text', text: 'Hello! Who is this for?' }] }] },
    'GET /state/sessions/S-1/stream.jsonl': '',
    ...Object.fromEntries(['reply', 'brief', 'write-specs', 'close', 'specs/1', 'specs/2'].map((a) => [`POST /api/sessions/S-1/${a}`, () => { n++; return { ok: true, message: a === 'brief' ? 'I-9 saved' : a.startsWith('specs') ? `J-${n}` : 'ok' } }])),
    ...extra,
  }, board)
  return calls
}
const openSession = async () => { fireEvent.click(await find('[data-session="S-1"]')); return find('[data-act]') }

describe('an idea session', () => {
  it('asks its questions and sends the answers', async () => {
    const calls = session(() => ({ ...summary, model: 'opus', pending: { id: 'q', name: 'ask_questions', input: { questions: [
      { question: 'Who uses it?', header: 'Users', options: [{ label: 'Just me', description: 'one' }, { label: 'A team' }] },
      { question: 'Which?', header: 'Pick', multiSelect: true, options: [{ label: 'a' }, { label: 'b' }] },
    ] } } }))
    const { $ } = renderApp()
    await openSession()
    await waitFor(() => expect($('[data-log]')).toHaveTextContent('Hello! Who is this for?'))
    expect($('[data-log]')).toHaveTextContent('Tag books')
    expect($('.sub-meta')).toHaveTextContent('S-1 · idle · target sim/sandbox · opus · $0.100')
    const q0 = (await find('[data-question="0"]'))
    fireEvent.click(q0.querySelector('[data-option="A team"]')!)
    fireEvent.click(q0.querySelector('[data-option="Just me"]')!)
    expect(q0.querySelector('[data-option="Just me"]')).toHaveClass('on')
    expect(q0.querySelector('[data-option="A team"]')).not.toHaveClass('on')
    const q1 = $('[data-question="1"]')!
    fireEvent.click(q1.querySelector('[data-option="a"]')!)
    fireEvent.click(q1.querySelector('[data-option="b"]')!)
    fireEvent.click(q1.querySelector('[data-option="b"]')!)
    fireEvent.change(q1.querySelector('[data-other]')!, { target: { value: 'c' } })
    fireEvent.click($('#s-send-answers')!)
    await waitFor(() => expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/sessions/S-1/reply', body: { text: 'Who uses it?\n→ Just me\n\nWhich?\n→ a; c' } }]))
  })

  it('confirms the brief, or sends a correction', async () => {
    const calls = session(() => ({ ...summary, pending: { id: 'b', name: 'submit_brief', input: { markdown: '# Brief: Tag books' } } }))
    const { $ } = renderApp()
    await openSession()
    await waitFor(() => expect($('[data-brief]')).toHaveTextContent('Tag books'))
    fireEvent.click($('#s-correct')!)
    expect($('#toast')).toHaveTextContent('Say what to correct')
    fireEvent.change($('#s-correction')!, { target: { value: 'tags are per user' } })
    fireEvent.click($('#s-correct')!)
    fireEvent.click($('#s-confirm-brief')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('Brief saved as I-9'))
    expect(writes(calls).map((c) => [c.url, c.body])).toEqual([['/api/sessions/S-1/reply', { text: 'tags are per user' }], ['/api/sessions/S-1/brief', undefined]])
  })

  it('a new target: confirms the proposed scaffold with the repo it names', async () => {
    const calls = session(() => ({ ...summary, target: undefined, pending: { id: 'p', name: 'propose_scaffold', input: { repo: 'recipes', name: 'recipes', resource: 'Recipe', db: 'sqlite', ui: 'react', events: false, auth: true } } }))
    const { $ } = renderApp()
    await openSession()
    await waitFor(() => expect($('#sc-repo')).toHaveValue('recipes'))
    expect($('.sub-meta')).toHaveTextContent('target pending')
    expect($('[data-scaffold]')).toHaveTextContent('sim/')
    expect($('#sc-auth')).toBeChecked()
    fireEvent.change($('#sc-repo')!, { target: { value: 'recipes-box' } })
    fireEvent.change($('#sc-db')!, { target: { value: 'postgres' } })
    fireEvent.change($('#sc-ui')!, { target: { value: 'blazor' } })
    fireEvent.change($('#sc-name')!, { target: { value: 'box' } })
    fireEvent.change($('#sc-resource')!, { target: { value: 'Card' } })
    fireEvent.click($('#sc-events')!)
    fireEvent.click($('#s-confirm-scaffold')!)
    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(writes(calls)[0].body).toEqual({
      text: 'Scaffold confirmed: repo recipes-box, name box, resource Card, data store postgres, front end blazor, with events.',
      scaffold: { repo: 'recipes-box', name: 'box', resource: 'Card', db: 'postgres', ui: 'blazor', events: true, auth: true },
    })
  })

  it('decides each spec, edits one, and closes the session when every spec is decided', async () => {
    const specs = (n: number) => [
      { k: 1, title: 'Tag a book', decision: n >= 1 ? 'approved' : undefined, job: n >= 1 ? 'J-2' : undefined },
      { k: 2, title: 'Filter by tag', decision: n >= 2 ? 'backlog' : undefined, job: n >= 2 ? 'J-3' : undefined },
    ]
    const calls = session((n) => ({ ...summary, idea: 'I-9', specs: specs(n) }), {
      'GET /state/ideas/I-9/spec-1.md': '# Spec: Tag a book',
      'GET /state/ideas/I-9/spec-2.md': '# Spec: Filter by tag',
    })
    const { $ } = renderApp()
    await openSession()
    await waitFor(() => expect($('[data-spec="1"] [data-spec-md]')).toHaveTextContent('Spec: Tag a book'))
    fireEvent.click($('[data-spec="1"] [data-edit]')!)
    await waitFor(() => expect($('[data-spec="1"] textarea')).toHaveValue('# Spec: Tag a book'))
    fireEvent.change($('[data-spec="1"] textarea')!, { target: { value: '# Spec: Tag a book, edited' } })
    fireEvent.click($('[data-spec="1"] [data-save]')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('Spec 1 saved'))
    fireEvent.click($('[data-spec="1"] [data-decide="approved"]')!)
    await waitFor(() => expect($('[data-spec="1"]')).toHaveTextContent('approved J-2'))
    fireEvent.click($('[data-spec="2"] [data-decide="backlog"]')!)
    await waitFor(() => expect($('#backdrop')).not.toHaveClass('open'))
    expect($('#toast')).toHaveTextContent('I-9 done: J-2 approved, J-3 in Backlog')
    expect(writes(calls).map((c) => [c.method, c.url, c.body])).toEqual([
      ['PUT', '/api/sessions/S-1/specs/1', '# Spec: Tag a book, edited'],
      ['POST', '/api/sessions/S-1/specs/1', { decision: 'approved' }],
      ['POST', '/api/sessions/S-1/specs/2', { decision: 'backlog' }],
      ['POST', '/api/sessions/S-1/close', { failed: false }],
    ])
  })

  it('every spec rejected closes the session as failed', async () => {
    const calls = session((n) => ({ ...summary, idea: 'I-9', specs: [{ k: 1, title: 'One', decision: n >= 1 ? 'rejected' : undefined }] }))
    const { $ } = renderApp()
    await openSession()
    fireEvent.click(await find('[data-spec="1"] [data-decide="rejected"]'))
    await waitFor(() => expect($('#toast')).toHaveTextContent('I-9 closed: every spec rejected'))
    expect(writes(calls).at(-1)).toEqual({ method: 'POST', url: '/api/sessions/S-1/close', body: { failed: true } })
  })

  it('a new-repo session shows its phase as a stepper, with no Approve or Write specs button', async () => {
    const boot = { ...summary, target: 'sim/shelf', newTarget: true, idea: 'I-9', bootstrap: 'J-1' }
    session(() => boot, {
      'GET /api/sessions/S-1': { ...boot, phase: { name: 'scaffold', job: 'J-1', stage: 'review', pr: null } },
      'GET /state/jobs/J-1/spec.md': '# Bootstrap',
    }, { jobs: [job('J-1', 'review', { kind: 'bootstrap' })], ideas: [{ id: 'I-9', text: 'x', status: 'waiting' }] })
    const { $, $$ } = renderApp()
    await openSession()
    await waitFor(() => expect($('[data-step="scaffold"]')).toHaveAttribute('data-state', 'now'))
    expect($$('[data-step]').map((e) => `${e.textContent}:${e.dataset.state}`)).toEqual(['Brief:done', 'Scaffold (J-1):now', 'Specs:todo', 'Build:todo'])
    expect($('[data-waiting]')).toHaveTextContent('J-1 is scaffolding shelf (in review). Nothing to do until it is ready for review.')
    await waitFor(() => expect($('[data-boot-spec]')).toHaveTextContent('Bootstrap'))
    expect($('#s-approve-boot')).toBeNull()
    expect($('#s-write-specs')).toBeNull()
    expect($('#s-merge-boot')).toBeNull()
  })

  it('a ready scaffold offers its PR and Merge, which is the usual move to Done', async () => {
    const boot = { ...summary, target: 'sim/shelf', newTarget: true, idea: 'I-9', bootstrap: 'J-1' }
    const calls = session(() => boot, {
      'GET /api/sessions/S-1': { ...boot, phase: { name: 'scaffold', job: 'J-1', stage: 'ready', pr: { number: 4, url: 'https://gh/pr/4' } } },
      'POST /api/jobs/J-1/move': { ok: true, message: 'merged' },
    }, { jobs: [job('J-1', 'ready', { kind: 'bootstrap', pr: { number: 4, url: 'https://gh/pr/4' } })], ideas: [] })
    const { $ } = renderApp()
    await openSession()
    await waitFor(() => expect($('[data-waiting]')).toHaveTextContent('J-1 is ready for review: merge the shelf scaffold.'))
    expect($('#s-boot-pr')).toHaveAttribute('href', 'https://gh/pr/4')
    expect($('[data-stepper]')).toHaveTextContent('next PR sync')
    fireEvent.click($('#s-merge-boot')!)
    await waitFor(() => expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/jobs/J-1/move', body: { to: 'done' } }]))
  })

  it('a new-repo session stays open once every spec is decided, showing the build', async () => {
    const boot = { ...summary, target: 'sim/shelf', newTarget: true, idea: 'I-9', bootstrap: 'J-1' }
    const specs = (n: number) => [{ k: 1, title: 'Shelve a book', decision: n >= 1 ? 'approved' : undefined, job: n >= 1 ? 'J-2' : undefined }]
    let n = 0
    const calls = session((m) => { n = m; return { ...boot, specs: specs(m), status: m >= 2 ? 'closed' : 'idle' } }, {
      'GET /api/sessions/S-1': () => (n >= 1
        ? { ...boot, phase: { name: 'build', jobs: [{ id: 'J-2', title: 'Shelve a book', stage: 'build', pr: null, deploy: null }] } }
        : { ...boot, phase: { name: 'specs' } }),
      'GET /state/ideas/I-9/spec-1.md': '# Spec: Shelve a book',
    })
    const { $ } = renderApp()
    await openSession()
    fireEvent.click(await find('[data-spec="1"] [data-decide="approved"]'))
    await waitFor(() => expect($('[data-step="build"]')).toHaveAttribute('data-state', 'now'))
    await waitFor(() => expect($('[data-build-job="J-2"]')).toHaveTextContent('J-2 Shelve a book building'))
    expect($('#backdrop')).toHaveClass('open')
    expect(writes(calls).map((c) => c.url)).toEqual(['/api/sessions/S-1/specs/1', '/api/sessions/S-1/close'])
  })

  it('an existing-repo session has no stepper', async () => {
    session(() => ({ ...summary }), { 'GET /api/sessions/S-1': { ...summary, phase: { name: 'brief' } } })
    const { $ } = renderApp()
    await openSession()
    await waitFor(() => expect($('[data-log]')).toHaveTextContent('Hello!'))
    expect($('[data-stepper]')).toBeNull()
  })

  it('a turn in error retries; a running turn shows the live stream; a closed session says so', async () => {
    const calls = session(() => ({ ...summary, status: 'error', error: 'model refused' }))
    const r = renderApp()
    await openSession()
    await waitFor(() => expect(r.$('.flagbox')).toHaveTextContent('model refused'))
    fireEvent.click(r.$('#s-retry')!)
    await waitFor(() => expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/sessions/S-1/reply', body: { text: '' } }]))
    r.unmount()

    session(() => ({ ...summary, status: 'running' }), { 'GET /state/sessions/S-1/stream.jsonl': '{"type":"text","text":"Thinking about it"}\n' })
    const r2 = renderApp()
    await openSession()
    await waitFor(() => expect(r2.$('[data-act]')).toHaveTextContent('Claude is working…'))
    expect(r2.$('.msg.live')).toHaveTextContent('Thinking about it')
    expect(r2.$('#session-abandon-btn')).toBeNull()
    r2.unmount()

    session(() => ({ ...summary, status: 'closed' }))
    const r3 = renderApp()
    await openSession()
    await waitFor(() => expect(r3.$('[data-act]')).toHaveTextContent('This session is closed.'))
  })

  it('Close session closes it as failed, and Abandon abandons it', async () => {
    const calls = session(() => ({ ...summary, base: 'J-4', newTarget: true }))
    const { $ } = renderApp()
    await openSession()
    await waitFor(() => expect($('.sub-meta')).toHaveTextContent('(new) · base factory/J-4'))
    fireEvent.click($('#s-close')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('S-1 closed'))
    expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/sessions/S-1/close', body: { failed: true, reason: 'closed in session before every spec was decided' } }])
    await openSession()
    fireEvent.click(await find('#session-abandon-btn'))
    await waitFor(() => expect(writes(calls).at(-1)).toEqual({ method: 'POST', url: '/api/sessions/S-1/close', body: { abandon: true } }))
  })

  it('a refused reply says why', async () => {
    session(() => ({ ...summary }), { 'POST /api/sessions/S-1/reply': { ok: false, message: 'a turn is running' } })
    const { $ } = renderApp()
    await openSession()
    fireEvent.change(await find('#s-msg'), { target: { value: 'hi' } })
    fireEvent.click($('#s-send')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('a turn is running'))
  })
})

describe('session notices', () => {
  const boot = { id: 'S-1', target: 'sim/shelf', text: 'A shelf', status: 'idle', idea: 'I-9', bootstrap: 'J-1', newTarget: true }

  it('a card says where its session is', async () => {
    fakeServer({ 'GET /api/sessions': [{ ...boot, phase: { name: 'scaffold', job: 'J-1', stage: 'ready', pr: null } }] })
    renderApp()
    await waitFor(async () => expect(await find('[data-session="S-1"] [data-session-state]')).toHaveTextContent('Ready: merge the scaffold'))
  })

  it('a notice recorded after the page loaded shows once, as a toast and a browser notification; older ones do not', async () => {
    const shown = vi.fn()
    vi.stubGlobal('Notification', Object.assign(function (this: unknown, title: string, o: { body: string }) { shown(title, o.body) }, { permission: 'granted', requestPermission: vi.fn() }))
    const future = new Date(Date.now() + 60_000).toISOString()
    let notices: Record<string, string> = { 'scaffold-ready': '2020-01-01T00:00:00Z' }
    fakeServer({ 'GET /api/sessions': () => [{ ...boot, notices, phase: { name: 'specs' } }] })
    const { $ } = renderApp()
    await find('[data-session="S-1"]')
    await new Promise((r) => setTimeout(r, 50))
    expect(shown).not.toHaveBeenCalled()
    notices = { ...notices, 'specs-ready': future }
    await waitFor(() => expect(shown).toHaveBeenCalledWith('Factory', 'Specs for I-9 are ready to approve'), { timeout: 4000 })
    expect($('#toast')).toHaveTextContent('Specs for I-9 are ready to approve')
    await new Promise((r) => setTimeout(r, 1700))
    expect(shown).toHaveBeenCalledTimes(1)
  })

  it('the scaffold notice names the job and the repo, with no notification unless granted', async () => {
    vi.stubGlobal('Notification', Object.assign(vi.fn(), { permission: 'default', requestPermission: vi.fn() }))
    const future = new Date(Date.now() + 60_000).toISOString()
    fakeServer({ 'GET /api/sessions': [{ ...boot, notices: { 'scaffold-ready': future }, phase: { name: 'scaffold', job: 'J-1', stage: 'ready', pr: null } }] })
    const { $ } = renderApp()
    await waitFor(() => expect($('#toast')).toHaveTextContent('J-1 is ready for review: merge the shelf scaffold'))
    expect(Notification).not.toHaveBeenCalled()
  })
})

describe('New idea', () => {
  it('lists the repos after Add new, offers unmerged jobs as a base, and starts a session', async () => {
    const calls = fakeServer({
      'GET /api/repos': ['sim/sandbox', 'sim/other'],
      'POST /api/sessions': { ok: true, message: 'S-7' },
      'GET /state/sessions/S-7/session.json': { ...summary, id: 'S-7' },
    }, { jobs: [job('J-1', 'ready', { pr: { number: 1, url: 'u' } }), job('J-2', 'done', { pr: { number: 2, url: 'u' } }), job('J-3', 'backlog')], ideas: [] })
    const { $, $$ } = renderApp()
    fireEvent.click(await find('#new-idea-btn'))
    await waitFor(() => expect($$('#s-target option').map((o) => o.getAttribute('value'))).toEqual(['+new', 'sim/sandbox', 'sim/other', '+other']))
    expect($('#s-base-row')).not.toBeVisible()
    fireEvent.change($('#s-target')!, { target: { value: 'sim/sandbox' } })
    expect($('#s-base-row')).toBeVisible()
    expect($$('#s-base option').map((o) => o.textContent)).toEqual(['main', 'J-1: job J-1 (unmerged, ready)'])
    fireEvent.change($('#s-base')!, { target: { value: 'J-1' } })
    fireEvent.change($('#s-text')!, { target: { value: ' tag list ' } })
    fireEvent.click($('#s-start')!)
    await waitFor(() => expect($('.sub-meta')).toHaveTextContent('S-7'))
    expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/sessions', body: { target: 'sim/sandbox', text: 'tag list', base: 'J-1' } }])
  })

  it('picking Add new says the bootstrap comes first, and asks once for notifications', async () => {
    const ask = vi.fn(async () => 'granted')
    const N = Object.assign(vi.fn(), { permission: 'default', requestPermission: ask })
    vi.stubGlobal('Notification', N)
    fakeServer({ 'GET /api/repos': ['sim/sandbox'], 'POST /api/sessions': { ok: false, message: 'stopped' } })
    const { $ } = renderApp()
    // Never on load.
    fireEvent.click(await find('#new-idea-btn'))
    await waitFor(() => expect($('#s-new-note')).toHaveTextContent('A new repo is set up first by a bootstrap job. Its feature specs are written after you merge it.'))
    expect(ask).not.toHaveBeenCalled()
    fireEvent.change($('#s-target')!, { target: { value: 'sim/sandbox' } })
    expect($('#s-new-note')).toBeNull()
    fireEvent.change($('#s-target')!, { target: { value: '+new' } })
    expect(ask).toHaveBeenCalledTimes(1)
    N.permission = 'granted'
    fireEvent.change($('#s-target')!, { target: { value: 'sim/sandbox' } })
    fireEvent.change($('#s-target')!, { target: { value: '+new' } })
    fireEvent.change($('#s-text')!, { target: { value: 'a shelf' } })
    fireEvent.click($('#s-start')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('stopped'))
    expect(ask).toHaveBeenCalledTimes(1)
  })

  it('starting a new repo straight away also asks, once, if it was never asked', async () => {
    const ask = vi.fn(async () => 'denied')
    vi.stubGlobal('Notification', Object.assign(vi.fn(), { permission: 'default', requestPermission: ask }))
    fakeServer({ 'GET /api/repos': [], 'POST /api/sessions': { ok: false, message: 'stopped' } })
    const { $ } = renderApp()
    fireEvent.click(await find('#new-idea-btn'))
    fireEvent.change(await find('#s-text'), { target: { value: 'a shelf' } })
    fireEvent.click($('#s-start')!)
    await waitFor(() => expect(ask).toHaveBeenCalledTimes(1))
  })

  it('Add new needs no repo; Other takes one; a refusal says why', async () => {
    const calls = fakeServer({ 'GET /api/repos': { ok: false, message: 'GitHub App not installed' }, 'POST /api/sessions': { ok: false, message: 'stopped' } })
    const { $ } = renderApp()
    fireEvent.click(await find('#new-idea-btn'))
    await waitFor(() => expect($('#s-repos-note')).toHaveTextContent('GitHub App not installed'))
    fireEvent.change($('#s-text')!, { target: { value: 'a recipe box' } })
    fireEvent.click($('#s-start')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('stopped'))
    fireEvent.change($('#s-target')!, { target: { value: '+other' } })
    expect($('#s-other')).toBeVisible()
    fireEvent.change($('#s-other')!, { target: { value: 'me/mine' } })
    fireEvent.click($('#s-start')!)
    await waitFor(() => expect(writes(calls).map((c) => c.body)).toEqual([{ new: true, text: 'a recipe box' }, { target: 'me/mine', text: 'a recipe box' }]))
  })
})

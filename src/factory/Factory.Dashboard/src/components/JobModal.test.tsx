import { fireEvent, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeServer, find, job, renderApp, writes } from '@/test/server'

let confirmAnswer = true
beforeEach(() => { confirmAnswer = true; vi.stubGlobal('confirm', () => confirmAnswer) })
afterEach(() => vi.unstubAllGlobals())

const open = async (id: string) => { fireEvent.click(await find(`[data-card="${id}"]`)); return find('#modal') }
const tab = (name: string) => fireEvent.click(document.querySelector(`[data-tab="${name}"]`)!)

describe('a backlog card', () => {
  const spec = '# Spec: J-5\n\n## Acceptance criteria\n- [ ] AC1'

  it('edits the spec beside a live preview, will not approve unsaved edits, and saves', async () => {
    const calls = fakeServer({ 'GET /state/jobs/J-5/spec.md': spec, 'PUT /api/jobs/J-5/spec': { ok: true, message: 'spec saved' } }, { jobs: [job('J-5', 'backlog')], ideas: [] })
    const { $ } = renderApp()
    await open('J-5')
    await waitFor(() => expect($('#spec-editor')).toHaveValue(spec))
    expect($('#spec-preview input[type="checkbox"]')).toBeInTheDocument()
    fireEvent.change($('#spec-editor')!, { target: { value: '# Spec: J-5\n\n## Intent\nEdited' } })
    expect($('#spec-preview h2')).toHaveTextContent('Intent')
    fireEvent.click($('#approve-btn')!)
    expect($('#toast')).toHaveTextContent('Save the spec first')
    fireEvent.click($('#spec-save')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('spec saved'))
    expect(writes(calls)).toEqual([{ method: 'PUT', url: '/api/jobs/J-5/spec', body: '# Spec: J-5\n\n## Intent\nEdited' }])
    fireEvent.click($('#approve-btn')!)
    expect($('#backdrop')).not.toHaveClass('open')
    await waitFor(() => expect(writes(calls).at(-1)).toEqual({ method: 'POST', url: '/api/jobs/J-5/move', body: { to: 'approved' } }))
  })

  it('closing with unsaved edits asks first, and keeps them when told to', async () => {
    fakeServer({ 'GET /state/jobs/J-5/spec.md': spec }, { jobs: [job('J-5', 'backlog')], ideas: [] })
    const { $ } = renderApp()
    await open('J-5')
    await waitFor(() => expect($('#spec-editor')).toHaveValue(spec))
    fireEvent.change($('#spec-editor')!, { target: { value: 'x' } })
    confirmAnswer = false
    fireEvent.keyDown(document, { key: 'Escape' })
    expect($('#backdrop')).toHaveClass('open')
    confirmAnswer = true
    fireEvent.click($('#backdrop')!, { detail: 1 })
    expect($('#backdrop')).not.toHaveClass('open')
  })

  it('a refused save says why, and an approve in flight spins on the card and cannot be sent twice', async () => {
    let answer: (v: Response) => void = () => {}
    const calls = fakeServer({
      'GET /state/jobs/J-5/spec.md': spec,
      'PUT /api/jobs/J-5/spec': { ok: false, message: 'edit refused' },
    }, { jobs: [job('J-5', 'backlog')], ideas: [] })
    const base = globalThis.fetch as unknown as (u: string, i?: RequestInit) => Promise<Response>
    vi.stubGlobal('fetch', vi.fn((u: string, i?: RequestInit) => (u.endsWith('/move') ? (calls.push({ method: 'POST', url: u }), new Promise<Response>((r) => { answer = r })) : base(u, i))))
    const { $ } = renderApp()
    await open('J-5')
    await waitFor(() => expect($('#spec-editor')).toHaveValue(spec))
    fireEvent.click($('#spec-save')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('edit refused'))
    fireEvent.click($('#approve-btn')!)
    await find('[data-card="J-5"] [data-moving] .spinner')
    expect($('[data-card="J-5"]')).toHaveAttribute('draggable', 'false')
    fireEvent.click($('[data-card="J-5"]')!)
    await waitFor(() => expect($('#approve-btn')).toBeDisabled())
    expect($('#discard-btn')).toBeDisabled()
    expect(calls.filter((c) => c.url.endsWith('/move'))).toHaveLength(1)
    answer(Response.json({ ok: false, message: 'approve refused: no spec' }))
    await waitFor(() => expect($('#toast')).toHaveTextContent('approve refused: no spec'))
    expect($('[data-card="J-5"] [data-moving]')).toBeNull()
  })
})

describe('a ready card', () => {
  it('shows the gate, PR and preview first; Rework needs a note; Merge closes on success', async () => {
    const calls = fakeServer({ 'GET /state/jobs/J-2/spec.md': '# Spec' }, {
      jobs: [job('J-2', 'ready', {
        idea: 'I-1', costUsd: 0.84, durationMs: 190000,
        pr: { number: 3, url: 'https://gh/3' }, gate: { build: '0', test: '0', at: 'x', sha: 'abcdef123' },
        deploy: { status: 'up', urls: { frontend: 'https://fe' } },
        flag: { reason: 'escalated', detail: 'd', why: 'Needs you:\n1. **Verify first**\n2. ship' },
      })], ideas: [],
    })
    const { $ } = renderApp()
    await open('J-2')
    expect($('.sub-meta')).toHaveTextContent('J-2 · ready · round 1 · from I-1 · $0.84 · 190s')
    expect($('[data-gate]')).toHaveTextContent('build=0 test=0 · abcdef1')
    expect($('[data-flagbox]')).toHaveTextContent('needs your decision')
    expect($('[data-flagbox] strong')).toHaveTextContent('Verify first')
    expect($('.actions > :first-child')).toHaveAttribute('id', 'preview-btn')
    expect($('#pr-btn')).toHaveAttribute('href', 'https://gh/3')
    fireEvent.click($('#rework-btn')!)
    expect($('#toast')).toHaveTextContent('A rework needs a note')
    fireEvent.change($('#rework-note')!, { target: { value: ' use the Input ' } })
    fireEvent.click($('#rework-btn')!)
    await waitFor(() => expect($('#backdrop')).not.toHaveClass('open'))
    expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/jobs/J-2/move', body: { to: 'approved', note: 'use the Input' } }])
    await open('J-2')
    fireEvent.click($('#merge-btn')!)
    expect($('#merge-btn')).toHaveTextContent('Merging…')
    await waitFor(() => expect($('#backdrop')).not.toHaveClass('open'))
    expect(writes(calls).at(-1)).toEqual({ method: 'POST', url: '/api/jobs/J-2/move', body: { to: 'done' } })
  })

  it('a refused merge or rework keeps the modal and the button comes back', async () => {
    fakeServer({ 'POST /api/jobs/J-2/move': { ok: false, message: 'gate red' } }, { jobs: [job('J-2', 'ready')], ideas: [] })
    const { $ } = renderApp()
    await open('J-2')
    fireEvent.click($('#merge-btn')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('gate red'))
    expect($('#merge-btn')).toHaveTextContent('Merge (Done)')
    fireEvent.change($('#rework-note')!, { target: { value: 'x' } })
    fireEvent.click($('#rework-btn')!)
    await waitFor(() => expect($('#rework-btn')).toHaveTextContent('Rework'))
    expect($('#backdrop')).toHaveClass('open')
  })

  it('a usage-limited card resumes from the modal', async () => {
    const calls = fakeServer({}, { jobs: [job('J-3', 'ready', { flag: { reason: 'usage-limit', detail: 'resets 12:30' } })], ideas: [] })
    const { $ } = renderApp()
    await open('J-3')
    expect($('[data-flagbox]')).toHaveTextContent('the same claude session')
    fireEvent.click($('#resume-btn')!)
    await waitFor(() => expect($('#backdrop')).not.toHaveClass('open'))
    expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/jobs/J-3/resume', body: {} }])
  })
})

describe('a blocked card', () => {
  it('blocked from deploy retries it from the modal; a refusal keeps the modal', async () => {
    fakeServer({ 'POST /api/jobs/J-4/redeploy': { ok: false, message: 'not from deploy' } }, {
      jobs: [job('J-4', 'blocked', { flag: { reason: 'environment', detail: 'boom' }, history: [{ from: 'deploy', to: 'blocked', by: 'orchestrator' }] })], ideas: [],
    })
    const { $ } = renderApp()
    await open('J-4')
    expect($('[data-flagbox]')).toHaveTextContent('cannot be fixed by an agent')
    expect($('[data-flagbox]')).toHaveTextContent('boom')
    fireEvent.click($('#redeploy-btn')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('not from deploy'))
    expect($('#redeploy-btn')).not.toBeDisabled()
    fireEvent.click($('#discard-btn')!)
  })
})

describe('a done card', () => {
  it('shows what the merge added and copies the paths', async () => {
    const writeText = vi.fn(async () => {})
    Object.assign(navigator, { clipboard: { writeText } })
    fakeServer({}, { jobs: [job('J-1', 'done', { merged: { sha: '8729d06aa', repo: 'sim/sandbox', pr: 'https://gh/1', added: ['a.txt'], modified: ['b.txt'] } })], ideas: [] })
    const { $ } = renderApp()
    await open('J-1')
    expect($('[data-merged]')).toHaveTextContent('Merged into sim/sandbox @ 8729d06 by pull request')
    fireEvent.click($('#copy-paths')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('Paths copied'))
    expect(writeText).toHaveBeenCalledWith('a.txt\nb.txt')
    Object.assign(navigator, { clipboard: { writeText: () => Promise.reject(new Error('no')) } })
    fireEvent.click($('#copy-paths')!)
    await waitFor(() => expect($('#toast')).toHaveTextContent('Copy failed'))
  })

  it('lists the artifacts, opens markdown and text, and keeps a timeline', async () => {
    const events = [
      { ts: '2026-11-01T12:00:00Z', kind: 'stage', job: 'J-1', from: 'build', to: 'review', by: 'orchestrator', reason: 'x' },
      { ts: '2026-11-01T12:00:01Z', kind: 'cost', job: 'J-1', costUsd: 0.42, durationMs: 3000 },
      { ts: '2026-11-01T12:00:02Z', kind: 'agent-start', job: 'J-1', agent: 'builder', round: 1, detail: 'd' },
      { ts: '2026-11-01T12:00:03Z', kind: 'idea-note', idea: 'I-1' },
    ].map((e) => JSON.stringify(e)).join('\n')
    fakeServer({
      'GET /state/events.jsonl': events,
      'GET /api/jobs/J-1/files': ['spec.md', 'round-1/build.md', 'gate.log'],
      'GET /state/jobs/J-1/round-1/build.md': '# Build: J-1',
      'GET /state/jobs/J-1/gate.log': 'ok',
    }, { jobs: [job('J-1', 'done', { idea: 'I-1' })], ideas: [] })
    const { $, $$ } = renderApp()
    await open('J-1')
    tab('artifacts')
    expect($('#viewer')).toHaveTextContent('Pick a file.')
    fireEvent.click(await find('[data-file="round-1/build.md"]'))
    await waitFor(() => expect($('#viewer h1')).toHaveTextContent('Build: J-1'))
    expect($('[data-file="round-1/build.md"]')).toHaveClass('on')
    fireEvent.click($('[data-file="gate.log"]')!)
    await waitFor(() => expect($('#viewer pre')).toHaveTextContent('ok'))
    fireEvent.click($('[data-file="spec.md"]')!)
    await waitFor(() => expect($('#viewer')).toHaveTextContent('(empty)'))
    tab('timeline')
    expect($$('[data-timeline] li').map((l) => l.querySelector('span')!.textContent)).toEqual([
      'build → review (orchestrator) [x]', 'cost $0.42, 3s', 'agent-start builder r1 — d', 'idea-note',
    ])
  })

  it('a job gone from the board says so', async () => {
    let jobs = [job('J-1', 'done')]
    fakeServer({ 'GET /state/board.json': () => ({ jobs, ideas: [] }) })
    const { $ } = renderApp()
    await open('J-1')
    jobs = []
    await waitFor(() => expect($('#m-head')).toHaveTextContent('J-1 is not on the board.'), { timeout: 3000 })
  })
})

describe('the Output tab', () => {
  it('opens first on a building job, tagging the orchestrator and each agent', async () => {
    const run = [
      '{"type":"assistant","message":{"content":[{"type":"text","text":"Starting J-1"},{"type":"tool_use","id":"t","name":"Agent","input":{"subagent_type":"factory:builder"}}]}}',
      '{"type":"result","subtype":"success","total_cost_usd":0.42,"duration_ms":1000}',
    ].join('\n')
    fakeServer({
      'GET /api/jobs/J-1/files': ['run-2.jsonl', 'run-1.jsonl'],
      'GET /state/jobs/J-1/run-1.jsonl': run,
      'GET /state/jobs/J-1/run-2.jsonl': '',
      'GET /state/events.jsonl': '{"kind":"agent-start","job":"J-1","agent":"builder","agentId":"a1","round":1}\n',
      'GET /api/agents/a1/transcript': '{"type":"assistant","message":{"content":[{"type":"thinking","thinking":""},{"type":"text","text":"Writing the failing test first"}]}}',
    }, { jobs: [job('J-1', 'build')], ideas: [] })
    const { $, $$ } = renderApp()
    await open('J-1')
    const out = await find('[data-output]')
    await waitFor(() => expect(out).toHaveTextContent('Writing the failing test first'))
    expect($$('[data-output] .sep').map((s) => s.textContent)).toEqual(['── run-1 ──', '── run-2 ──', '── builder · round 1 ──'])
    expect(out).toHaveTextContent('▸ Agent(factory:builder)')
    expect(out).toHaveTextContent('$0.42')
    expect($('[data-output] .thinking')).toBeInTheDocument()
    expect($('[data-output] [data-agent="builder"]')).toBeInTheDocument()
    fireEvent.scroll(out)
  })

  it('says so when there is no output yet', async () => {
    fakeServer({}, { jobs: [job('J-1', 'review')], ideas: [] })
    renderApp()
    await open('J-1')
    await waitFor(async () => expect(await find('[data-output]')).toHaveTextContent('No output yet.'))
  })
})

describe('an idea card', () => {
  it('opens its brief, its spec-writer output, and abandons at once', async () => {
    const calls = fakeServer({
      'GET /state/ideas/I-1/brief.md': '# Brief\nwhich list, and what counts as fast',
      'GET /state/events.jsonl': '{"kind":"agent-start","idea":"I-1","agent":"spec-writer","agentId":"s1"}\n',
      'GET /api/agents/s1/transcript': '{"type":"assistant","message":{"content":[{"type":"text","text":"Writing specs"}]}}',
    }, { jobs: [], ideas: [{ id: 'I-1', text: 'Make it faster', status: 'failed', target: 'sim/sandbox', jobs: ['J-1'], reason: 'too vague' }] })
    const { $ } = renderApp()
    fireEvent.click(await find('[data-idea="I-1"] .title'))
    await waitFor(() => expect($('[data-brief]')).toHaveTextContent('which list, and what counts as fast'))
    expect($('.sub-meta')).toHaveTextContent('I-1 · failed · target sim/sandbox · J-1')
    expect($('.flagbox')).toHaveTextContent('too vague')
    tab('output')
    await waitFor(() => expect($('[data-output]')).toHaveTextContent('Writing specs'))
    fireEvent.click($('#idea-abandon-btn')!)
    await waitFor(() => expect($('#backdrop')).not.toHaveClass('open'))
    expect(writes(calls)).toEqual([{ method: 'POST', url: '/api/ideas/I-1/abandon', body: {} }])
  })

  it('with no brief says so, and a done idea cannot be abandoned', async () => {
    fakeServer({}, { jobs: [], ideas: [{ id: 'I-2', text: 'x', status: 'drafting' }] })
    const { $ } = renderApp()
    fireEvent.click(await find('[data-idea="I-2"]'))
    await waitFor(() => expect($('[data-brief]')).toHaveTextContent('(no brief)'))
    expect($('#idea-abandon-btn')).toBeInTheDocument()
  })
})

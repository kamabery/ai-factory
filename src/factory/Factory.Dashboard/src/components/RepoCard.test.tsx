import { fireEvent, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeServer, find, job, renderApp, writes } from '@/test/server'

afterEach(() => vi.unstubAllGlobals())

const product = (next: number | null, ideas: (string | null)[] = [null, null]) => ({
  features: [
    { k: 1, heading: 'Add a todo', text: 'Type it in.', idea: ideas[0] },
    { k: 2, heading: 'Share a list', text: 'With a friend.', idea: ideas[1] },
  ],
  next, home: '- Home: redirect: /todos',
})

const repoSession = (phase: object) => ({
  id: 'S-1', kind: 'repo', idea: 'I-1', bootstrap: 'J-1', text: 'a todo app', target: 'sim/todo', status: 'idle', newTarget: true, phase,
})

const board = (ideaStatus = 'drafting') => ({
  jobs: [job('J-1', ideaStatus === 'drafting' ? 'done' : 'build', { idea: 'I-1', kind: 'bootstrap' })],
  ideas: [{ id: 'I-1', text: 'a todo app', status: ideaStatus, target: 'sim/todo', kind: 'repo', features: [], jobs: ['J-1'] }],
})

// Spec 22: a New repo idea's card in Ideas runs Grill → Scaffold → Review → Merged, then starts each feature.
describe('the repo card', () => {
  it('is marked New repo and shows its steps with the current one lit', async () => {
    fakeServer({ 'GET /api/sessions': [repoSession({ name: 'scaffold', job: 'J-1', stage: 'build', pr: null })] }, board('waiting'))
    const r = renderApp()
    const card = await find('[data-session="S-1"]')
    expect(card.querySelector('[data-repo-chip]')).toHaveTextContent('New repo')
    expect([...card.querySelectorAll('[data-repo-step]')].map((s) => `${s.textContent}:${(s as HTMLElement).dataset.state}`))
      .toEqual(['Grill:done', 'Scaffold:now', 'Review:todo', 'Merged:todo'])
    expect(r.$('[data-implement]')).toBeNull()
  })

  it('once merged offers Implement first feature, which opens the feature session', async () => {
    const calls = fakeServer({
      'GET /api/sessions': [repoSession({ name: 'merged' })],
      'GET /api/ideas/I-1/product': product(1),
      'POST /api/ideas/I-1/implement': { ok: true, message: 'S-2' },
      'GET /state/sessions/S-2/session.json': { id: 'S-2', text: 'Add a todo', status: 'running', target: 'sim/todo', from: 'I-1', feature: 'Add a todo' },
    }, board())
    renderApp()
    const button = await find('[data-session="S-1"] [data-implement]')
    expect(button).toHaveTextContent('Implement first feature')
    expect(await find('[data-session="S-1"] [data-next-feature]')).toHaveTextContent('Add a todo')
    fireEvent.click(button)
    await waitFor(() => expect(writes(calls)).toContainEqual({ method: 'POST', url: '/api/ideas/I-1/implement', body: {} }))
    await waitFor(() => expect(document.querySelector('#modal')).toHaveTextContent('S-2'))
  })

  it('names a later feature, and Dismiss closes the repo idea', async () => {
    const calls = fakeServer({
      'GET /api/sessions': [repoSession({ name: 'merged' })],
      'GET /api/ideas/I-1/product': product(2, ['I-2', null]),
    }, board())
    renderApp()
    expect(await find('[data-session="S-1"] [data-implement]')).toHaveTextContent('Implement Share a list')
    fireEvent.click(await find('[data-session="S-1"] [data-dismiss]'))
    await waitFor(() => expect(writes(calls)).toContainEqual({ method: 'POST', url: '/api/ideas/I-1/dismiss', body: {} }))
  })

  it('shows the open feature session in place of Implement', async () => {
    fakeServer({
      'GET /api/sessions': [repoSession({ name: 'merged' }), { id: 'S-2', from: 'I-1', feature: 'Add a todo', text: 'Add a todo', target: 'sim/todo', status: 'idle', phase: { name: 'brief' } }],
      'GET /api/ideas/I-1/product': product(1),
    }, board())
    const r = renderApp()
    expect(await find('[data-session="S-1"] [data-feature-open]')).toHaveTextContent('S-2 is working on Add a todo')
    expect(r.$('[data-session="S-1"] [data-implement]')).toBeNull()
  })

  it('says why when PRODUCT.md cannot be read, and offers Dismiss only', async () => {
    fakeServer({
      'GET /api/sessions': [repoSession({ name: 'merged' })],
      'GET /api/ideas/I-1/product': { ok: false, message: 'product refused: docs/PRODUCT.md on main has no feature to start' },
    }, board())
    const r = renderApp()
    expect(await find('[data-session="S-1"] [data-product-error]')).toHaveTextContent('docs/PRODUCT.md on main has no feature to start')
    expect(r.$('[data-session="S-1"] [data-implement]')).toBeNull()
    expect(r.$('[data-session="S-1"] [data-dismiss]')).toBeInTheDocument()
  })

  it('a repo idea with no open session gets the same actions on its idea card', async () => {
    fakeServer({ 'GET /api/ideas/I-1/product': product(1) }, board())
    renderApp()
    expect(await find('[data-idea="I-1"] [data-implement]')).toHaveTextContent('Implement first feature')
    expect(await find('[data-idea="I-1"] [data-repo-chip]')).toHaveTextContent('New repo')
  })
})

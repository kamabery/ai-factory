import { describe, expect, it } from 'vitest'
import { cardState, phaseSteps, waitingText, type Phase } from './session'

const scaffold = (stage: string): Phase => ({ name: 'scaffold', job: 'J-2', stage, pr: null })

describe('the session stepper', () => {
  it('lights the current phase; the ones before it are done', () => {
    expect(phaseSteps({ name: 'brief' }, 'J-2')).toEqual([
      { key: 'brief', label: 'Brief', state: 'now' }, { key: 'scaffold', label: 'Scaffold (J-2)', state: 'todo' },
      { key: 'specs', label: 'Specs', state: 'todo' }, { key: 'build', label: 'Build', state: 'todo' },
    ])
    expect(phaseSteps({ name: 'specs' }, 'J-2').map((s) => s.state)).toEqual(['done', 'done', 'now', 'todo'])
    expect(phaseSteps({ name: 'build', jobs: [] }, 'J-2').map((s) => s.state)).toEqual(['done', 'done', 'done', 'now'])
    // Before the brief is confirmed there is no bootstrap yet.
    expect(phaseSteps({ name: 'brief' }, null)[1].label).toBe('Scaffold')
  })

  it('says in plain text what each phase waits on', () => {
    const s = { status: 'idle', target: 'sim/shelf', newTarget: true }
    expect(waitingText({ name: 'brief' }, { ...s, pending: { name: 'submit_brief' } })).toBe('Confirm the brief below. Confirming it also approves the bootstrap job that creates the repo.')
    expect(waitingText({ name: 'brief' }, s)).toBe('Answer Claude below: it grills you, then proposes a brief.')
    expect(waitingText(scaffold('build'), s)).toBe('J-2 is scaffolding shelf (building). Nothing to do until it is ready for review.')
    expect(waitingText(scaffold('review'), s)).toBe('J-2 is scaffolding shelf (in review). Nothing to do until it is ready for review.')
    expect(waitingText(scaffold('ready'), s)).toBe('J-2 is ready for review: merge the shelf scaffold. Claude then writes the specs, with no click.')
    expect(waitingText(scaffold('backlog'), s)).toBe('J-2 was not approved: its approval was refused. Approve it on its row in Ideas once that is fixed.')
    expect(waitingText(scaffold('blocked'), s)).toBe('J-2 is blocked: open it on the board to see why.')
    expect(waitingText({ name: 'specs' }, { ...s, status: 'running' })).toBe('Claude is reading the new repo and writing the specs.')
    expect(waitingText({ name: 'specs' }, { ...s, specs: [{ k: 1 }] })).toBe('Decide each spec below.')
    expect(waitingText({ name: 'specs' }, { ...s, status: 'error' })).toBe('Writing the specs failed: Retry below.')
    expect(waitingText({ name: 'specs' }, s)).toBe('Starting the specs…')
    expect(waitingText({ name: 'build', jobs: [{ id: 'J-3', stage: 'build' }, { id: 'J-4', stage: 'backlog' }] }, s)).toBe('The approved specs are building: follow them here or on the board.')
    expect(waitingText({ name: 'build', jobs: [{ id: 'J-4', stage: 'backlog' }] }, s)).toBe('The specs wait for approval: approve them on this card in Ideas when you are ready.')
  })
})

describe('the session card', () => {
  const x = { id: 'S-1', text: 't', status: 'idle', bootstrap: 'J-2', target: 'sim/shelf', newTarget: true }
  it('says where a new-repo session is, in one line', () => {
    expect(cardState({ ...x, phase: scaffold('review') })).toBe('waiting for bootstrap J-2 (in review)')
    expect(cardState({ ...x, phase: scaffold('approved') })).toBe('waiting for bootstrap J-2 (approved)')
    expect(cardState({ ...x, phase: scaffold('ready') })).toBe('Ready: merge the scaffold')
    expect(cardState({ ...x, status: 'running', phase: { name: 'specs' } })).toBe('writing specs')
    expect(cardState({ ...x, phase: { name: 'specs' }, notices: { 'specs-ready': 't' } })).toBe('specs ready to approve')
  })
  it('otherwise says whose turn it is', () => {
    expect(cardState({ ...x, bootstrap: null, status: 'running', phase: { name: 'brief' } })).toBe('Claude is working')
    expect(cardState({ ...x, bootstrap: null, phase: { name: 'brief' } })).toBe('your turn')
    expect(cardState({ ...x, bootstrap: null })).toBe('your turn')
  })
})

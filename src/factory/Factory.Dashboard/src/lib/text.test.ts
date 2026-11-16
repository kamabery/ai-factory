import { describe, expect, it } from 'vitest'
import { md } from './markdown'
import { resultText, streamLines, summarize } from './stream'
import { answersText, money, parseStream, scaffoldText, sessionLog, toolLine } from './session'
import { evalRows, feedbackBody, parseFeedback, pendingEvals, proposeText } from './evals'
import type { Job } from './types'

describe('markdown', () => {
  it('renders GFM, shows raw HTML as text, and keeps backslashes literally', () => {
    expect(md('## Head\n- [ ] AC1')).toContain('<h2>Head</h2>')
    expect(md('- [ ] AC1')).toContain('type="checkbox"')
    expect(md('<script>alert(1)</script>')).not.toContain('<script>')
    expect(md('<script>alert(1)</script>')).toContain('&lt;script&gt;')
    expect(md('D:\\code\\.x')).toContain('D:\\code\\.x')
    expect(md(undefined)).toBe('')
  })
})

describe('the Output tab', () => {
  it('summarizes a tool call by its most telling input, cut at 160', () => {
    expect(summarize({ command: 'git  status' })).toBe('git status')
    expect(summarize({ other: 1 })).toBe('{"other":1}')
    expect(summarize(null)).toBe('')
    expect(summarize({ command: 'x'.repeat(200) })).toHaveLength(161)
  })

  it('reads a tool result from a string or blocks, cut at 200', () => {
    expect(resultText([{ text: 'a' }, { text: 'b' }])).toBe('a b')
    expect(resultText('  x\n y ')).toBe('x y')
    expect(resultText(42)).toBe('')
    expect(resultText('y'.repeat(300))).toHaveLength(201)
  })

  it('turns stream-json into tagged lines, naming agents by their Agent call', () => {
    const agents: Record<string, string> = {}
    const lines = [
      '{"type":"system","subtype":"init","model":"opus"}',
      '{"type":"assistant","message":{"content":[{"type":"text","text":"Starting J-001"},{"type":"tool_use","id":"t1","name":"Agent","input":{"subagent_type":"factory:builder","description":"build it"}}]}}',
      '{"type":"assistant","parent_tool_use_id":"t1","message":{"content":[{"type":"thinking","thinking":""},{"type":"thinking","thinking":"hm"}]}}',
      '{"type":"user","message":{"content":[{"type":"tool_result","content":"done"},{"type":"text","text":"note"}]}}',
      '{"type":"user","message":{"content":"plain"}}',
      '{"type":"result","subtype":"success","total_cost_usd":0.42,"duration_ms":3000}',
      'not json',
      '   ',
    ].flatMap((l) => streamLines(l, 'orchestrator', agents))
    expect(lines.map((l) => [l.who, l.cls, l.text])).toEqual([
      ['orchestrator', 'dim', '● session started (opus)'],
      ['orchestrator', '', 'Starting J-001'],
      ['orchestrator', 'tool', '▸ Agent(build it)'],
      ['builder', 'thinking', '💭 thinking…'],
      ['builder', 'thinking', '💭 hm'],
      ['orchestrator', 'res', '  ↳ done'],
      ['orchestrator', 'dim', '› note'],
      ['orchestrator', 'dim', '› plain'],
      ['orchestrator', 'done', '■ success · $0.42 · 3s'],
      ['orchestrator', 'dim', 'not json'],
    ])
  })
})

describe('idea sessions', () => {
  it('money shows cents under a dollar to three places', () => {
    expect(money(0.1234)).toBe('$0.123')
    expect(money(2)).toBe('$2.00')
    expect(money(undefined)).toBe('$0.000')
  })

  it('names each tool the model used', () => {
    expect(toolLine({ name: 'ask_questions', input: { questions: [{ question: 'Who?' }, { question: 'Why?' }] } })).toBe('? Who? · Why?')
    expect(toolLine({ name: 'submit_brief', input: {} })).toBe('▸ proposed the brief')
    expect(toolLine({ name: 'propose_scaffold', input: { name: 'r', resource: 'R', db: 'sqlite', ui: 'react', events: false, auth: true } }))
      .toBe('▸ proposed the scaffold: r / R / sqlite / react / no events / sign-in')
    expect(toolLine({ name: 'propose_scaffold', input: { events: true, auth: false } })).toContain('events / no sign-in')
    expect(toolLine({ name: 'submit_specs', input: { specs: [{}, {}] } })).toBe('▸ wrote 2 spec(s)')
    expect(toolLine({ name: 'revise_spec', input: { k: 2 } })).toBe('▸ revised spec 2')
    expect(toolLine({ name: 'read_file', input: { path: 'README.md' } })).toBe('▸ read_file(README.md)')
    expect(toolLine({ name: 'grep' })).toBe('▸ grep()')
  })

  it('the log shows the human, Claude, tools, the confirmed brief and errors; the live turn comes from the stream', () => {
    const chat = [
      { role: 'user', content: 'first prompt the model saw' },
      { role: 'assistant', content: [{ type: 'text', text: 'Hi' }, { type: 'tool_use', id: 'q', name: 'ask_questions', input: { questions: [{ question: 'Who?' }] } }, { type: 'tool_use', id: 'b', name: 'submit_brief', input: {} }, { type: 'tool_use', id: 'r', name: 'read_file', input: { path: 'x' } }] },
      { role: 'user', content: [
        { type: 'tool_result', tool_use_id: 'q', content: 'Just me' },
        { type: 'tool_result', tool_use_id: 'b', content: 'The human confirmed the brief.' },
        { type: 'tool_result', tool_use_id: 'r', content: 'no such file', is_error: true },
        { type: 'tool_result', tool_use_id: 'r', content: 'quiet' },
        { type: 'text', text: 'later' },
      ] },
    ]
    const stream = parseStream('{"type":"text","text":"Work"}\n{"type":"tool","name":"grep","summary":"x"}\n{"type":"text","text":"ing"}\nbad\n')
    const log = sessionLog({ text: 'The idea', status: 'running', idea: 'I-9' }, chat, stream)
    expect(log).toEqual([
      { kind: 'you', text: 'The idea' },
      { kind: 'claude', text: 'Hi' },
      { kind: 'tool', text: '? Who?' },
      { kind: 'tool', text: '▸ proposed the brief' },
      { kind: 'tool', text: '▸ read_file(x)' },
      { kind: 'you', text: 'Just me' },
      { kind: 'tool', text: '✓ brief confirmed as I-9' },
      { kind: 'tool', text: '  ↳ no such file' },
      { kind: 'you', text: 'later' },
      { kind: 'claude', text: 'Work' },
      { kind: 'tool', text: '▸ grep(x)' },
      { kind: 'claude', text: 'ing', live: true },
    ])
    expect(sessionLog({ text: 't', status: 'idle' }, [], stream)).toEqual([])
    expect(sessionLog({ text: 't', status: 'running' }, [], [])).toEqual([{ kind: 'claude', text: '', live: true }])
  })

  it('answers name each question and the picks, with Other last', () => {
    expect(answersText([{ question: 'Who?' }, { question: 'Why?' }], [['Just me'], []], ['', ' dunno '])).toBe('Who?\n→ Just me\n\nWhy?\n→ dunno')
    expect(answersText([{ question: 'Who?' }], [[]], [''])).toBe('Who?\n→ (no answer)')
  })

  it('the scaffold reply restates every option', () => {
    const s = { name: 'r', resource: 'R', db: 'sqlite', ui: 'react', events: false, auth: true }
    expect(scaffoldText({ ...s, repo: 'recipes' })).toBe('Scaffold confirmed: repo recipes, name r, resource R, data store sqlite, front end react, no events.')
    expect(scaffoldText({ ...s, events: true })).toBe('Scaffold confirmed: name r, resource R, data store sqlite, front end react, with events.')
  })
})

describe('evaluations and lessons', () => {
  const j = (id: string, stage: string): Job => ({ id, stage, round: 1, title: id })

  it('every finished job with no evaluation is pending, and the rows are newest job first', () => {
    const list = [{ job: 'J-002', rating: 'mixed', title: 'J-002' }]
    const jobs = [j('J-001', 'done'), j('J-002', 'done'), j('J-010', 'discarded'), j('J-005', 'backlog')]
    expect(pendingEvals(list, jobs).map((x) => x.job)).toEqual(['J-001', 'J-010'])
    expect(evalRows(list, jobs).map((x) => [x.job, x.rating])).toEqual([['J-010', 'not evaluated'], ['J-002', 'mixed'], ['J-001', 'not evaluated']])
  })

  it('feedback round-trips as a rating, a blank line and the note', () => {
    expect(feedbackBody('poor', 'split it')).toBe('Rating: poor\n\nsplit it\n')
    expect(parseFeedback('Rating: poor\r\n\r\nsplit it\n')).toEqual({ rating: 'poor', note: 'split it' })
    expect(parseFeedback('')).toEqual({ rating: '', note: '' })
  })

  it('a template fix proposal carries every selected lesson', () => {
    const text = proposeText(['J-008', 'J-002'], ['one', 'two'])
    expect(text).toMatch(/^Template fixes from factory lessons \(J-008, J-002\)\./)
    expect(text).toContain('one\n\n---\n\ntwo')
  })
})

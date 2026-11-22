// Idea sessions: the log and the replies the panel sends (contract, "Idea sessions (dashboard)").
import { leaf } from './board'
import { resultText } from './stream'
import type { Phase, SessionSummary } from './types'

export type { Phase }

const STEPS = ['brief', 'scaffold', 'specs', 'build'] as const
export type Step = { key: (typeof STEPS)[number]; label: string; state: 'done' | 'now' | 'todo' }

/** The new-repo stepper: Brief → Scaffold (J-NNN) → Specs → Build, the current phase lit. */
export function phaseSteps(phase: Phase, bootstrap?: string | null): Step[] {
  const at = STEPS.indexOf(phase.name)
  return STEPS.map((key, i) => ({
    key,
    label: key === 'scaffold' && bootstrap ? `Scaffold (${bootstrap})` : key[0].toUpperCase() + key.slice(1),
    state: i < at ? 'done' : i === at ? 'now' : 'todo',
  }))
}

/** A stage as a phrase: "J-2 is scaffolding shelf (in review)". */
export const stageText = (stage?: string) => ({
  backlog: 'waiting for approval', approved: 'approved', build: 'building', review: 'in review', deploy: 'deploying', approve: 'with the approver',
  gate: 'at the gate', blocked: 'blocked', ready: 'ready for review', done: 'merged', discarded: 'discarded',
} as Record<string, string>)[stage ?? ''] ?? stage ?? 'not on the board'

type Waiting = { status: string; target?: string; pending?: { name: string } | null; specs?: unknown[] }

/** What the current phase waits on, in plain text (never only a tooltip). */
export function waitingText(phase: Phase, s: Waiting): string {
  const repo = leaf(s.target ?? '')
  switch (phase.name) {
    case 'brief':
      if (s.pending?.name === 'submit_brief') return 'Confirm the brief below. Confirming it also approves the bootstrap job that creates the repo.'
      return s.status === 'running' ? 'Claude is working.' : 'Answer Claude below: it grills you, then proposes a brief.'
    case 'scaffold':
      if (phase.stage === 'ready') return `${phase.job} is ready for review: merge the ${repo} scaffold. Claude then writes the specs, with no click.`
      if (phase.stage === 'backlog') return `${phase.job} was not approved: its approval was refused. Approve it on its row in Ideas once that is fixed.`
      if (phase.stage === 'blocked') return `${phase.job} is blocked: open it on the board to see why.`
      if (phase.stage === 'discarded') return `${phase.job} was discarded: this repo has no scaffold.`
      return `${phase.job} is scaffolding ${repo} (${stageText(phase.stage)}). Nothing to do until it is ready for review.`
    case 'specs':
      if (s.status === 'running') return 'Claude is reading the new repo and writing the specs.'
      if (s.specs?.length) return 'Decide each spec below.'
      if (s.status === 'error') return 'Writing the specs failed: Retry below.'
      return 'Starting the specs…'
    case 'build':
      return phase.jobs?.length && phase.jobs.every((j) => j.stage === 'backlog')
        ? 'The specs wait for approval: approve them on this card in Ideas when you are ready.'
        : 'The approved specs are building: follow them here or on the board.'
  }
}

/** The session card's one line: where the session is, or whose turn it is. */
export function cardState(x: Pick<SessionSummary, 'status' | 'phase' | 'notices' | 'bootstrap'>): string {
  const p = x.phase
  if (p?.name === 'scaffold') return p.stage === 'ready' ? 'Ready: merge the scaffold' : `waiting for bootstrap ${p.job} (${stageText(p.stage)})`
  if (x.status === 'running') return p?.name === 'specs' && x.bootstrap ? 'writing specs' : 'Claude is working'
  if (p?.name === 'specs' && x.notices?.['specs-ready']) return 'specs ready to approve'
  return 'your turn'
}

/** The two moments a new-repo session tells the human about (contract, "Advancing and notices"). */
export function noticeText(kind: string, x: Pick<SessionSummary, 'phase' | 'bootstrap' | 'target' | 'idea'>): string {
  return kind === 'scaffold-ready'
    ? `${x.bootstrap ?? x.phase?.job} is ready for review: merge the ${leaf(x.target ?? '')} scaffold`
    : `Specs for ${x.idea} are ready to approve`
}

/** The tools whose answer is the human's, shown as the human's message. */
const UI_TOOLS = ['ask_questions', 'propose_scaffold', 'submit_brief']

export const money = (v: number | undefined | null) => `$${Number(v ?? 0).toFixed(Number(v ?? 0) < 1 ? 3 : 2)}`

export type ToolUse = { name?: string; input?: Record<string, unknown> }

type ToolInput = {
  questions?: { question: string }[]; specs?: unknown[]; k?: number; path?: string; pattern?: string
  name?: string; resource?: string; db?: string; ui?: string; events?: boolean; auth?: boolean
}

export function toolLine(b: ToolUse): string {
  const i = (b.input ?? {}) as ToolInput
  if (b.name === 'ask_questions') return `? ${(i.questions ?? []).map((q) => q.question).join(' · ')}`
  if (b.name === 'submit_brief') return '▸ proposed the brief'
  if (b.name === 'propose_scaffold') return `▸ proposed the scaffold: ${[i.name, i.resource, i.db, i.ui, i.events ? 'events' : 'no events', i.auth ? 'sign-in' : 'no sign-in'].join(' / ')}`
  if (b.name === 'submit_specs') return `▸ wrote ${(i.specs ?? []).length} spec(s)`
  if (b.name === 'revise_spec') return `▸ revised spec ${i.k}`
  return `▸ ${b.name}(${i.path ?? i.pattern ?? ''})`
}

export type ChatBlock = {
  type?: string; text?: string; id?: string; name?: string; input?: Record<string, unknown>
  tool_use_id?: string; content?: unknown; is_error?: boolean
}
export type ChatMessage = { role: string; content: string | ChatBlock[] }
export type StreamEvent = { type?: string; text?: string; name?: string; summary?: string }
export type LogEntry = { kind: 'you' | 'claude' | 'tool'; text: string; live?: boolean }

export const parseStream = (text: string): StreamEvent[] =>
  text.split('\n').filter(Boolean).flatMap((l) => { try { return [JSON.parse(l)] } catch { return [] } })

/** The session log: chat.json, then (while a turn runs) the turn so far from stream.jsonl. */
export function sessionLog(s: { text: string; status: string; idea?: string | null }, chat: ChatMessage[], stream: StreamEvent[]): LogEntry[] {
  const names: Record<string, string | undefined> = {}
  for (const m of chat) if (m.role === 'assistant' && Array.isArray(m.content)) for (const b of m.content) if (b.type === 'tool_use') names[String(b.id)] = b.name
  const out: LogEntry[] = []
  chat.forEach((m, mi) => {
    const blocks: ChatBlock[] = typeof m.content === 'string' ? [{ type: 'text', text: m.content }] : m.content ?? []
    for (const b of blocks) {
      const tool = names[String(b.tool_use_id)]
      if (m.role === 'user') {
        // The first message is the prompt the model got; the human typed the session's text.
        if (b.type === 'text') out.push({ kind: 'you', text: mi === 0 ? s.text : String(b.text) })
        else if (b.type === 'tool_result' && tool === 'submit_brief' && /^The human confirmed/.test(String(b.content))) out.push({ kind: 'tool', text: `✓ brief confirmed${s.idea ? ` as ${s.idea}` : ''}` })
        else if (b.type === 'tool_result' && UI_TOOLS.includes(String(tool))) out.push({ kind: 'you', text: String(b.content) })
        else if (b.type === 'tool_result' && b.is_error) out.push({ kind: 'tool', text: `  ↳ ${resultText(b.content)}` })
      } else if (b.type === 'text' && b.text) out.push({ kind: 'claude', text: b.text })
      else if (b.type === 'tool_use') out.push({ kind: 'tool', text: toolLine(b) })
    }
  })
  if (s.status === 'running') {
    // chat.json gets this turn when it ends.
    let text = ''
    const flush = (live: boolean) => { if (text || live) out.push(live ? { kind: 'claude', text, live } : { kind: 'claude', text }); text = '' }
    for (const e of stream) {
      if (e.type === 'text') text += e.text ?? ''
      else if (e.type === 'tool') { flush(false); out.push({ kind: 'tool', text: `▸ ${e.name}(${e.summary ?? ''})` }) }
    }
    flush(true)
  }
  return out
}

/** The reply to ask_questions: each question, then its picks and the Other text. */
export function answersText(questions: { question: string }[], picks: string[][], others: string[]): string {
  return questions.map((q, qi) => {
    const all = [...(picks[qi] ?? [])]
    const other = (others[qi] ?? '').trim()
    if (other) all.push(other)
    return `${q.question}\n→ ${all.join('; ') || '(no answer)'}`
  }).join('\n\n')
}

export type Scaffold = { repo?: string; name: string; resource: string; db: string; ui: string; events: boolean; auth: boolean }

export const scaffoldText = (s: Scaffold) =>
  `Scaffold confirmed: ${s.repo ? `repo ${s.repo}, ` : ''}name ${s.name}, resource ${s.resource}, data store ${s.db}, front end ${s.ui}, ${s.events ? 'with' : 'no'} events.`

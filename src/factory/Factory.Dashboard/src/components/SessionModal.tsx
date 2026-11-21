import { useLayoutEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { keys, POLL_MS, useConfig } from '@/app/data'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { control } from '@/components/ui/control'
import { api, getJson, getText } from '@/lib/api'
import { cn } from '@/lib/cn'
import { md } from '@/lib/markdown'
import { answersText, money, parseStream, scaffoldText, sessionLog, type ChatMessage, type Scaffold, type StreamEvent } from '@/lib/session'
import type { Result, SessionSummary } from '@/lib/types'
import { Actions, Body, CloseButton, FlagBox, Head, Md, SubMeta, Title } from './Modal'
import { SessionStepper } from './SessionStepper'

type Question = { question: string; header?: string; multiSelect?: boolean; options?: { label: string; description?: string }[] }
type SpecEntry = { k: number; title: string; decision?: string; job?: string }
export type SessionFile = {
  id?: string; text: string; status: string; target?: string; newTarget?: boolean; base?: string; model?: string
  costUsd?: number; idea?: string | null; bootstrap?: string | null; error?: string
  pending?: { id: string; name: string; input?: { questions?: Question[]; markdown?: string } & Partial<Scaffold> } | null
  specs?: SpecEntry[]
}
type Sess = { s: SessionFile; chat: ChatMessage[]; stream: StreamEvent[] }

const RUNNING_POLL_MS = 400   // while a turn runs, follow its stream more closely than the board poll does

const sessionKey = (id: string) => ['session', id]
const summaryKey = (id: string) => ['session-summary', id]

/** GET /api/sessions/<S>: the session with its phase, which the server works out from the board (closed or not). */
function useSummary(id: string, running: boolean) {
  return useQuery({
    queryKey: summaryKey(id),
    queryFn: () => getJson<SessionSummary>(`/api/sessions/${id}`),
    refetchInterval: running ? RUNNING_POLL_MS : POLL_MS,
    retry: false,
  }).data
}

/** session.json, chat.json and stream.jsonl under /state/sessions/<S>/. An unreadable session.json keeps the last read. */
function useSession(id: string) {
  return useQuery({
    queryKey: sessionKey(id),
    queryFn: async (): Promise<Sess> => {
      const [s, chat, stream] = await Promise.all(['session.json', 'chat.json', 'stream.jsonl'].map((f) => getText(`/state/sessions/${id}/${f}`)))
      const parsed = JSON.parse(s) as SessionFile
      let messages: ChatMessage[] = []
      try { messages = JSON.parse(chat).messages ?? [] } catch { /* none yet */ }
      return { s: parsed, chat: messages, stream: parseStream(stream) }
    },
    refetchInterval: (q) => (q.state.data?.s.status === 'running' ? RUNNING_POLL_MS : POLL_MS),
    retry: false,
  }).data
}

/** One session endpoint, then a fresh read of the session; returns the answer and that read. */
function useSessApi(id: string) {
  const qc = useQueryClient()
  const { toast } = useUi()
  return async (action: string, body?: unknown, method = 'POST', type = 'application/json'): Promise<{ r: Result; s?: SessionFile }> => {
    const r = await api(method, `/api/sessions/${id}/${action}`, body, type)
    if (!r.ok) toast(r.message || 'refused', true)
    await Promise.all([sessionKey(id), summaryKey(id), keys.sessions].map((queryKey) => qc.invalidateQueries({ queryKey })))
    return { r, s: qc.getQueryData<Sess>(sessionKey(id))?.s }
  }
}

export function SessionModal({ id }: { id: string }) {
  const sess = useSession(id)
  const s = sess?.s
  const phase = useSummary(id, s?.status === 'running')?.phase
  const { abandon, closeModal, toast, refresh } = useUi()
  const sessApi = useSessApi(id)
  const [busy, setBusy] = useState(false)
  const canClose = s && s.status !== 'running' && s.status !== 'closed'
  const closeSession = async () => {
    const { r } = await sessApi('close', { failed: true, reason: 'closed in session before every spec was decided' })
    if (r.ok) { toast(`${id} closed`); closeModal(true); await refresh() }
  }
  return (
    <>
      <Head>
        <CloseButton />
        <Title>{s?.text ?? id}</Title>
        <SubMeta>
          {id}{s?.idea && ` · ${s.idea}`} · {s?.status ?? '…'}
          {s?.target ? <> · target <code>{s.target}</code>{s.newTarget && ' (new)'}</> : s && ' · target pending'}
          {s?.base && <> · base <code>factory/{s.base}</code></>}
          {s?.model && <> · <code>{s.model}</code></>}
          {' · '}<span data-cost>{money(s?.costUsd)}</span>
          {canClose && <> · <Button id="s-close" variant="danger" size="sm" onClick={closeSession}>Close session</Button></>}
        </SubMeta>
        {canClose && (
          <Actions><Button id="session-abandon-btn" variant="danger" disabled={busy} onClick={() => { setBusy(true); abandon(id, true) }}>Abandon</Button></Actions>
        )}
      </Head>
      <Body>
        <div className="flex h-full min-h-0 flex-col gap-2.5">
          <Log sess={sess} />
          <div className="max-h-[60%] overflow-auto border-t border-border-subtle pt-2.5" data-act>
            {s?.newTarget && phase && <SessionStepper s={s} phase={phase} />}
            {s && <SessionActions id={id} s={s} sessApi={sessApi} />}
          </div>
        </div>
      </Body>
    </>
  )
}

function Log({ sess }: { sess?: Sess }) {
  const el = useRef<HTMLDivElement>(null)
  const atBottom = useRef(true)
  const log = sess ? sessionLog(sess.s, sess.chat, sess.stream) : []
  useLayoutEffect(() => { if (atBottom.current && el.current) el.current.scrollTop = el.current.scrollHeight }, [sess])
  return (
    <div ref={el} data-log className="flex min-h-0 flex-1 flex-col gap-2 overflow-auto pr-1"
      onScroll={(e) => { const p = e.currentTarget; atBottom.current = p.scrollHeight - p.scrollTop - p.clientHeight < 60 }}>
      {log.map((e, i) => e.kind === 'you'
        ? <div key={i} className="msg you max-w-[85%] self-end rounded-surface bg-brand-subtle px-3 py-2 whitespace-pre-wrap">{e.text}</div>
        : e.kind === 'claude'
          ? <Md key={i} html={md(e.text)} className={cn('msg claude max-w-[85%] self-start rounded-surface bg-surface-sunken px-3 py-2 [&>p:first-child]:mt-0 [&>p:last-child]:mb-0', e.live && 'live')} />
          : <div key={i} className="tool-ln self-start font-mono text-xs whitespace-pre text-content-muted">{e.text}</div>)}
    </div>
  )
}

type SessApi = ReturnType<typeof useSessApi>

function SessionActions({ id, s, sessApi }: { id: string; s: SessionFile; sessApi: SessApi }) {
  const [msg, setMsg] = useState('')
  const p = s.pending
  if (s.status === 'closed') return <p>This session is closed.{s.newTarget && s.specs?.some((x) => x.job) && ' Its specs are on the board.'}</p>
  if (s.status === 'running') return <div className="working text-intent-running">Claude is working…</div>
  const specs = s.specs ?? []
  return (
    <>
      {s.status === 'error' && <><FlagBox>{s.error}</FlagBox><Actions><Button id="s-retry" variant="secondary" onClick={() => sessApi('reply', { text: '' })}>Retry</Button></Actions></>}
      {p?.name === 'ask_questions' ? <QuestionsForm key={p.id} questions={p.input?.questions ?? []} sessApi={sessApi} />
        : p?.name === 'propose_scaffold' ? <ScaffoldForm key={p.id} proposal={p.input ?? {}} hasTarget={!!s.target} sessApi={sessApi} />
        : p?.name === 'submit_brief' ? <BriefForm key={p.id} markdown={p.input?.markdown ?? ''} sessApi={sessApi} />
        : specs.length ? <SpecsForm id={id} s={s} sessApi={sessApi} /> : null}
      {!p && (
        <Actions className="items-end">
          <textarea id="s-msg" rows={2} value={msg} onChange={(e) => setMsg(e.target.value)}
            placeholder={specs.length ? 'Ask Claude to change a spec (say which)' : 'Message Claude'}
            className={cn(control, 'w-auto flex-1')} />
          <Button id="s-send" variant="secondary" onClick={() => { if (msg.trim()) { sessApi('reply', { text: msg.trim() }); setMsg('') } }}>Send</Button>
        </Actions>
      )}
    </>
  )
}


function QuestionsForm({ questions, sessApi }: { questions: Question[]; sessApi: SessApi }) {
  const [picks, setPicks] = useState<string[][]>(() => questions.map(() => []))
  const [others, setOthers] = useState<string[]>(() => questions.map(() => ''))
  const toggle = (qi: number, label: string, multi: boolean) => setPicks((all) => all.map((p, i) => {
    if (i !== qi) return p
    if (p.includes(label)) return p.filter((x) => x !== label)
    return multi ? [...p, label] : [label]
  }))
  return (
    <div data-questions>
      {questions.map((q, qi) => (
        <div key={qi} className="q mb-2 rounded-surface border border-border-subtle px-3 py-2.5" data-question={qi} data-multi={q.multiSelect ? 1 : 0}>
          <div className="mb-1.5 flex items-baseline gap-2"><span className="chip">{q.header}</span><b>{q.question}</b>{q.multiSelect && <small>pick any</small>}</div>
          <div className="mb-1.5 flex flex-wrap gap-1.5">
            {(q.options ?? []).map((o) => (
              <button key={o.label} type="button" data-option={o.label} onClick={() => toggle(qi, o.label, !!q.multiSelect)}
                className={cn('opt max-w-[280px] rounded-control border px-3 py-1.5 text-left',
                  picks[qi].includes(o.label) ? 'on border-brand bg-brand-subtle' : 'border-border-subtle bg-surface-sunken')}>
                {o.label}<small className="block text-[11.5px] text-content-muted">{o.description ?? ''}</small>
              </button>
            ))}
          </div>
          <input type="text" data-other className={control} placeholder={'Other: your own answer, or "I don\'t know"'}
            value={others[qi]} onChange={(e) => setOthers((all) => all.map((x, i) => (i === qi ? e.target.value : x)))} />
        </div>
      ))}
      <Actions><Button id="s-send-answers" onClick={() => sessApi('reply', { text: answersText(questions, picks, others) })}>Send answers</Button></Actions>
    </div>
  )
}

function ScaffoldForm({ proposal, hasTarget, sessApi }: { proposal: Partial<Scaffold>; hasTarget: boolean; sessApi: SessApi }) {
  const { config } = useConfig()
  const [v, setV] = useState<Scaffold>({
    repo: proposal.repo, name: proposal.name ?? '', resource: proposal.resource ?? '', db: proposal.db ?? 'sqlite',
    ui: proposal.ui ?? 'react', events: !!proposal.events, auth: !!proposal.auth,
  })
  const set = (patch: Partial<Scaffold>) => setV((x) => ({ ...x, ...patch }))
  const withRepo = proposal.repo !== undefined && !hasTarget
  const confirm = () => {
    const scaffold: Scaffold = { name: v.name.trim(), resource: v.resource.trim(), db: v.db, ui: v.ui, events: v.events, auth: v.auth }
    if (withRepo) scaffold.repo = (v.repo ?? '').trim()
    sessApi('reply', { text: scaffoldText(scaffold), scaffold })
  }
  const select = (id: string, value: string, options: string[], key: 'db' | 'ui') => (
    <select id={id} value={value} onChange={(e) => set({ [key]: e.target.value })} className={control}>
      {options.map((o) => <option key={o}>{o}</option>)}
    </select>
  )
  return (
    <>
      <p>A new target is scaffolded from template-projects. Check the options Claude proposed:</p>
      <div className="grid max-w-[640px] grid-cols-[140px_1fr] items-center gap-x-3 gap-y-2" data-scaffold>
        {withRepo && <><label htmlFor="sc-repo">Repo</label><div className="flex items-center gap-1">{config.owner}/<input id="sc-repo" className={control} value={v.repo ?? ''} onChange={(e) => set({ repo: e.target.value })} /></div></>}
        <label htmlFor="sc-name">Name</label><input id="sc-name" className={control} value={v.name} onChange={(e) => set({ name: e.target.value })} />
        <label htmlFor="sc-resource">Resource</label><input id="sc-resource" className={control} value={v.resource} onChange={(e) => set({ resource: e.target.value })} />
        <label htmlFor="sc-db">Data store</label>{select('sc-db', v.db, ['sqlite', 'sqlserver', 'postgres'], 'db')}
        <label htmlFor="sc-ui">Front end</label>{select('sc-ui', v.ui, ['react', 'blazor', 'blazor-maui', 'none'], 'ui')}
        <label htmlFor="sc-events">Events</label><div><input type="checkbox" id="sc-events" checked={v.events} onChange={(e) => set({ events: e.target.checked })} /></div>
        <label htmlFor="sc-auth">Sign-in</label>
        <div><input type="checkbox" id="sc-auth" checked={v.auth} onChange={(e) => set({ auth: e.target.checked })} /> <small>Keycloak locally, Entra External ID in Azure; needs a web front end</small></div>
        <span /><Actions className="mt-0"><Button id="s-confirm-scaffold" onClick={confirm}>Confirm scaffold</Button></Actions>
      </div>
    </>
  )
}

function BriefForm({ markdown, sessApi }: { markdown: string; sessApi: SessApi }) {
  const { toast } = useUi()
  const [correction, setCorrection] = useState('')
  const [busy, setBusy] = useState(false)
  const confirm = async () => {
    setBusy(true)
    const { r } = await sessApi('brief')
    if (r.ok) toast(`Brief saved as ${r.message.split(' ')[0]}`); else setBusy(false)
  }
  return (
    <>
      <p>Read the brief. Confirm it, or say what is wrong and Claude revises it.</p>
      <Md html={md(markdown)} data-brief className="s-brief mb-2 rounded-surface border border-border-subtle px-3.5 py-1" />
      <textarea id="s-correction" rows={3} value={correction} onChange={(e) => setCorrection(e.target.value)} placeholder="What is wrong or missing?" className={control} />
      <Actions>
        <Button id="s-confirm-brief" disabled={busy} onClick={confirm}>Confirm brief</Button>
        <Button id="s-correct" variant="secondary" onClick={() => (correction.trim() ? sessApi('reply', { text: correction.trim() }) : toast('Say what to correct', true))}>Send correction</Button>
      </Actions>
    </>
  )
}

function useText(url: string | null, key: unknown[]) {
  return useQuery({ queryKey: ['text', url, ...key], queryFn: () => getText(url!), enabled: !!url }).data
}

function SpecsForm({ id, s, sessApi }: { id: string; s: SessionFile; sessApi: SessApi }) {
  const { toast, closeModal, refresh } = useUi()
  const specs = s.specs ?? []
  const decide = async (sp: SpecEntry, decision: string) => {
    const { r, s: fresh } = await sessApi(`specs/${sp.k}`, { decision })
    if (r.ok) toast(decision === 'rejected' ? `Spec ${sp.k} rejected` : `${r.message} → ${decision === 'approved' ? 'Approved' : 'Backlog'}`)
    const now = fresh?.specs ?? []
    if (!now.length || !now.every((x) => x.decision)) return
    const kept = now.filter((x) => x.decision !== 'rejected')
    const { r: c } = await sessApi('close', { failed: !kept.length })
    if (!c.ok) return
    toast(kept.length ? `${fresh?.idea} done: ${kept.map((x) => `${x.job} ${x.decision === 'approved' ? 'approved' : 'in Backlog'}`).join(', ')}` : `${fresh?.idea} closed: every spec rejected`)
    // A new-repo session stays open on its Build phase: the stepper follows the jobs it made.
    if (!(s.newTarget && kept.length)) closeModal(true)
    await refresh()
  }
  return (
    <>
      <p>Decide each spec. <b>Approve</b> builds it now, <b>Not yet</b> puts it in Backlog, <b>Reject</b> drops it. To change one, edit it, or ask Claude below.</p>
      {specs.map((sp) => <SpecCard key={sp.k} id={id} idea={s.idea} sp={sp} decide={decide} sessApi={sessApi} />)}
    </>
  )
}

function SpecCard({ id, idea, sp, decide, sessApi }: {
  id: string; idea?: string | null; sp: SpecEntry; decide: (sp: SpecEntry, d: string) => Promise<void>; sessApi: SessApi
}) {
  const { toast } = useUi()
  const qc = useQueryClient()
  const url = `/state/ideas/${idea}/spec-${sp.k}.md`
  const text = useText(url, [id, sp.title, sp.decision])
  const [editing, setEditing] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState(!sp.decision)
  const save = async () => {
    const { r } = await sessApi(`specs/${sp.k}`, editing ?? '', 'PUT', 'text/markdown')
    if (r.ok) { toast(`Spec ${sp.k} saved`); setEditing(null); await qc.invalidateQueries({ queryKey: ['text', url] }) }
  }
  return (
    <div className={cn('spec-card mb-2 rounded-surface border border-border-subtle px-3 py-2', sp.decision && 'decided opacity-75')} data-spec={sp.k}>
      <details open={open || editing !== null} onToggle={(e) => setOpen(e.currentTarget.open)}>
        <summary className="cursor-pointer font-semibold">
          Spec {sp.k}: {sp.title}{sp.decision && <> <span className="chip">{sp.decision}{sp.job ? ` ${sp.job}` : ''}</span></>}
        </summary>
        {editing !== null ? (
          <div data-spec-md>
            <textarea className={cn(control, 'spec-edit min-h-[260px] font-mono text-[12.5px]')} spellCheck={false} value={editing} onChange={(e) => setEditing(e.target.value)} />
            <Actions><Button data-save onClick={save}>Save spec</Button></Actions>
          </div>
        ) : <Md html={text === undefined ? '…' : md(text)} data-spec-md />}
      </details>
      {!sp.decision && (
        <Actions>
          <Button data-decide="approved" disabled={busy} onClick={() => { setBusy(true); decide(sp, 'approved') }}>Approve</Button>
          <Button data-decide="backlog" variant="secondary" disabled={busy} onClick={() => { setBusy(true); decide(sp, 'backlog') }}>Not yet</Button>
          <Button data-decide="rejected" variant="danger" disabled={busy} onClick={() => { setBusy(true); decide(sp, 'rejected') }}>Reject</Button>
          <Button data-edit variant="secondary" disabled={busy} onClick={async () => setEditing(await getText(url))}>Edit</Button>
        </Actions>
      )}
    </div>
  )
}

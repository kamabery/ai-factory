import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { control } from '@/components/ui/control'
import { api, enc, getJson, getText } from '@/lib/api'
import { duration, leaf } from '@/lib/board'
import { cn } from '@/lib/cn'
import { evalRows, feedbackBody, parseFeedback, type EvalEntry } from '@/lib/evals'
import { md } from '@/lib/markdown'
import { Actions } from './Modal'

export const VIEW_POLL_MS = 3000
const usd = (n: number | null | undefined) => (n != null ? `$${Number(n).toFixed(2)}` : '—')

export function ListPane({ children }: { children: React.ReactNode }) {
  return <div className="eval-list flex flex-col gap-2">{children}</div>
}

export function BodyPane({ children, ...data }: { children: React.ReactNode } & Record<`data-${string}`, unknown>) {
  return <div className="eval-body md min-h-[200px] rounded-surface border border-border-subtle bg-surface-raised px-4 py-1" {...data}>{children}</div>
}

export const Empty = ({ children }: { children: React.ReactNode }) => <div className="eval-empty py-3 text-content-muted">{children}</div>

function EvalRow({ x, open, onOpen }: { x: EvalEntry; open: boolean; onOpen: () => void }) {
  const facts = [
    x.stage, x.round != null ? `r${x.round}` : '', x.flag ? <span key="f" className="flag">{x.flag}</span> : '',
    x.gate ? `gate ${x.gate}` : '', x.merged ? `merged ${String(x.merged).slice(0, 7)}` : '',
    x.costUsd != null ? `$${Number(x.costUsd).toFixed(2)}` : '', duration(x.durationMs),
  ].filter(Boolean)
  return (
    <div className={cn('card eval-row', open && 'selected')} data-eval={x.job} data-pending={x.pending ? '' : undefined} onClick={onOpen}>
      <div className="top"><span>{x.job}{x.idea ? ` · ${x.idea}` : ''}</span><span>{String(x.evaluatedAt ?? '').slice(0, 10)}</span></div>
      <div className="title">{x.title}</div>
      {x.target && <div className="repo" title={x.target}>→ {leaf(x.target)}</div>}
      <div className="meta">
        <span className={cn('chip', `rating-${x.rating}`)} data-rating>{x.rating}</span>
        {x.human && <span className={cn('chip', `rating-${x.human.rating}`)} data-human-rating>you: {x.human.rating}</span>}
        {facts.map((f, i) => <span key={i} className="whitespace-nowrap">{i > 0 && ' · '}{f}</span>)}
      </div>
    </div>
  )
}

function Fact({ k, label, value, sub, cls }: { k: string; label: string; value: string; sub?: string; cls?: string }) {
  return (
    <div className="fact min-w-0 rounded-control border border-border-subtle bg-surface-sunken px-2.5 py-2" data-fact={k}>
      <div className="text-[11px] tracking-wide text-content-muted uppercase">{label}</div>
      <div className={cn('text-lg font-semibold break-words tabular-nums', cls === 'green' && 'text-intent-success', cls === 'red' && 'text-intent-danger')}>{value}</div>
      {sub && <div className="text-[11.5px] text-content-muted">{sub}</div>}
    </div>
  )
}

function Bar({ v, max }: { v: number; max: number }) {
  return <span className="col-span-2 h-1 rounded-pill bg-brand opacity-70" style={{ width: `${Math.max(2, Math.round((100 * v) / max))}%` }} />
}

/** Gate, cost and time: the snapshot's facts as a grid, above the evaluation's markdown. */
function EvalFacts({ x }: { x: EvalEntry }) {
  const agents = x.agents ?? []
  const totalAgentMs = agents.reduce((s, a) => s + (a.ms ?? 0), 0)
  const maxAgentMs = Math.max(1, ...agents.map((a) => a.ms ?? 0))
  const models = Object.entries(x.models ?? {})
  const maxModelUsd = Math.max(0.0001, ...models.map(([, m]) => m.costUsd ?? 0))
  const row = 'grid grid-cols-[minmax(0,1fr)_auto] gap-x-2 gap-y-0.5 py-0.5 text-[12.5px]'
  return (
    <section className="facts mt-3 mb-4 border-b border-border-subtle pb-3.5" data-eval-facts="">
      <h2 className="!mt-0 mb-2.5 text-base">Gate, cost and time</h2>
      <div className="grid grid-cols-[repeat(auto-fit,minmax(120px,1fr))] gap-2">
        <Fact k="gate" label="Gate" value={x.gate ?? 'not run'} sub={x.decision ? `approver ${x.decision}` : ''} cls={x.gate} />
        <Fact k="cost" label="Cost" value={usd(x.costUsd)} sub={models.length ? `${models.length} model${models.length > 1 ? 's' : ''}` : ''} />
        <Fact k="time" label="Time" value={duration(x.durationMs) || '—'} sub="runner wall time" />
        <Fact k="rounds" label="Rounds" value={String(x.round ?? '—')} sub={x.flag ? `flag ${x.flag}` : 'no flag'} />
        <Fact k="agent-time" label="Agent time" value={duration(totalAgentMs) || '—'} sub={`summed over ${agents.length} agent runs; reviewers overlap`} />
        <Fact k="gate-time" label="Gate time" value={duration(x.gateMs) || '—'} />
        <Fact k="outcome" label="Outcome" value={x.stage ?? '—'} sub={x.merged ? `merged ${String(x.merged).slice(0, 7)}` : ''} />
      </div>
      {(models.length > 0 || agents.length > 0) && (
        <div className="mt-2.5 grid grid-cols-1 gap-3 md:grid-cols-2">
          <div>
            <h3 className="!mt-0 mb-1.5 text-xs tracking-wide text-content-muted uppercase">Cost by model</h3>
            {models.length ? models.map(([m, u]) => (
              <div key={m} className={row} data-model={m}>
                <span className="truncate font-mono">{m}</span>
                <span className="text-right tabular-nums">{usd(u.costUsd)} · {Number(u.outputTokens ?? 0).toLocaleString()} out</span>
                <Bar v={u.costUsd ?? 0} max={maxModelUsd} />
              </div>
            )) : <div className="text-[11.5px] text-content-muted">no run logs</div>}
          </div>
          <div>
            <h3 className="!mt-0 mb-1.5 text-xs tracking-wide text-content-muted uppercase">Time by agent</h3>
            {agents.length ? agents.map((a, i) => (
              <div key={i} className={row} data-agent={a.agent}>
                <span className="truncate font-mono">{a.agent} r{a.round}</span>
                <span className="text-right tabular-nums">{duration(a.ms) || '0s'}</span>
                <Bar v={a.ms ?? 0} max={maxAgentMs} />
              </div>
            )) : <div className="text-[11.5px] text-content-muted">no agent events</div>}
          </div>
        </div>
      )}
    </section>
  )
}

/** The human's rating and note; the draft is the human's until saved, whatever the poll reads. */
function FeedbackForm({ id }: { id: string }) {
  const { toast } = useUi()
  const qc = useQueryClient()
  const [fb, setFb] = useState<{ rating: string; note: string } | null>(null)
  useEffect(() => { getText(`/state/evals/${enc(id)}.feedback.md`).then((t) => setFb(parseFeedback(t))) }, [id])
  if (!fb) return null
  const save = async () => {
    if (!fb.rating) { toast('Pick a rating first', true); return }
    const res = await api('PUT', `/api/evals/${enc(id)}/feedback`, feedbackBody(fb.rating, fb.note), 'text/markdown')
    toast(res.message, !res.ok)
    if (res.ok) await qc.invalidateQueries({ queryKey: ['evals'] })
  }
  return (
    <section className="feedback mt-4 mb-3 border-t border-border-subtle pt-3.5" data-feedback>
      <h2 className="!mt-0 mb-2.5 text-base">Human feedback</h2>
      <div className="mb-2 flex gap-1.5">
        {['good', 'mixed', 'poor'].map((r) => (
          <Button key={r} type="button" variant="secondary" data-human={r} className={cn(fb.rating === r && 'on border-brand')} onClick={() => setFb({ ...fb, rating: r })}>{r}</Button>
        ))}
      </div>
      <textarea rows={4} value={fb.note} onChange={(e) => setFb({ ...fb, note: e.target.value })} placeholder="Your hands-on check: does it work, and was it worth the run?"
        className={control} />
      <Actions><Button data-save-feedback onClick={save}>Save feedback</Button></Actions>
    </section>
  )
}

export function Evals() {
  const { board } = useData()
  const [open, setOpen] = useState<string | null>(null)
  const list = useQuery({ queryKey: ['evals'], queryFn: async () => (await getJson<EvalEntry[]>('/api/evals')) ?? [], refetchInterval: VIEW_POLL_MS }).data ?? []
  const rows = evalRows(list, board.jobs)
  const x = list.find((e) => e.job === open)
  const pending = rows.find((e) => e.job === open && e.pending)
  const body = useQuery({ queryKey: ['eval-md', open], queryFn: () => getText(`/state/evals/${enc(open!)}.md`), enabled: !!x, refetchInterval: VIEW_POLL_MS }).data
  return (
    <main id="evals" className="grid grid-cols-1 items-start gap-3 p-4 md:grid-cols-[minmax(320px,2fr)_3fr]">
      <ListPane>
        {rows.length ? rows.map((r) => <EvalRow key={r.job} x={r} open={open === r.job} onOpen={() => setOpen(r.job)} />)
          : <Empty>No evaluations yet. A finished job is evaluated with <code>factory eval &lt;id&gt; -File &lt;md&gt;</code>.</Empty>}
      </ListPane>
      <BodyPane data-eval-body>
        {pending ? (
          <p className="eval-empty py-3 text-content-muted">
            {pending.job} is {pending.stage} and not evaluated yet; Clear keeps it on the board until it is. Evaluate it in a Claude Code session
            with <code>factory eval {pending.job} -File &lt;md&gt;</code>.
          </p>
        ) : x ? (
          <><EvalFacts x={x} /><div dangerouslySetInnerHTML={{ __html: md(body ?? '') }} /><FeedbackForm key={x.job} id={x.job} /></>
        ) : <p className="eval-empty py-3 text-content-muted">Pick an evaluation.</p>}
      </BodyPane>
    </main>
  )
}


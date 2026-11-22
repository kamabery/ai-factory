import { useUi } from '@/app/ui'
import { leaf } from '@/lib/board'
import { cardState, stageText } from '@/lib/session'
import { cn } from '@/lib/cn'
import type { Idea, SessionSummary } from '@/lib/types'
import { SpecRows } from './SpecRows'

/** An Ideas card's Abandon: the first click asks, a second within 4 s abandons (contract, "Ideas"). */
function AbandonButton({ id }: { id: string }) {
  const { armed, abandon } = useUi()
  const on = armed === id
  return (
    <button type="button" data-abandon={id} onClick={(e) => { e.stopPropagation(); abandon(id) }}
      className={cn('chip action cursor-pointer bg-surface-raised', on ? 'danger border-intent-danger text-intent-danger' : 'text-content')}>
      {on ? 'Abandon?' : 'Abandon'}
    </button>
  )
}

/** Beside Abandon: whose move it is (contract, "Advancing and notices"). The factory's gets a moving marker. */
const YOURS = ['your turn', 'Ready: merge the scaffold', 'specs ready to approve']
function WaitChip({ text, running }: { text: string; running?: boolean }) {
  const yours = YOURS.includes(text)
  return (
    <span data-session-state data-wait={yours ? 'you' : 'factory'}
      className={cn('chip inline-flex items-center gap-1.5', running && 'running', yours ? 'border-brand font-semibold text-brand' : 'text-content')}>
      {yours ? <span aria-hidden className="inline-block size-2 shrink-0 rounded-full bg-brand" /> : <span data-spinner aria-hidden className="inline-block size-2 shrink-0 animate-pulse rounded-full bg-intent-running" />}
      {text}
    </span>
  )
}

export function IdeaCard({ i, stage }: { i: Idea; stage?: string }) {
  const { openModal } = useUi()
  return (
    <div className={cn('card idea-card', i.status, i.status === 'failed' && 'border-intent-danger')} data-idea={i.id} onClick={() => openModal('idea', i.id)}>
      <div className="top">
        <span>{i.id}</span>
        <span>{i.status === 'failed' ? 'failed' : i.status === 'waiting' ? '' : i.closeWhenDecided ? 'specs to approve' : <>spec it: <code>/factory:idea {i.id}</code></>}</span>
      </div>
      <div className="title !font-medium">{i.text}</div>
      {i.target && <div className="repo" title={i.target}>→ {leaf(i.target)}</div>}
      {i.reason && <div className="meta">{i.reason}</div>}
      <div className="meta">
        {i.base && <span className="chip kind">on {i.base}</span>}
        {i.status === 'waiting' && <WaitChip text={`waiting for bootstrap ${i.jobs?.[0] ?? ''} (${stageText(stage)})`} />}
        <AbandonButton id={i.id} />
      </div>
      <SpecRows idea={i.id} />
    </div>
  )
}

export function SessionCard({ x }: { x: SessionSummary }) {
  const { openModal } = useUi()
  return (
    <div className="card idea-card session-card border-l-[3px] border-l-brand" data-session={x.id} onClick={() => openModal('session', x.id)}>
      <div className="top">
        <span>{x.id}{x.idea ? ` · ${x.idea}` : ''}</span>
        <span>{x.costUsd ? `$${Number(x.costUsd).toFixed(2)}` : ''}</span>
      </div>
      <div className="title !font-medium">{x.text}</div>
      {x.target && <div className="repo" title={x.target}>→ {leaf(x.target)}</div>}
      <div className="meta">
        {x.status === 'error' ? <span className="flag">error</span>
          : <WaitChip text={cardState(x)} running={x.status === 'running'} />}
        {x.status !== 'running' && <AbandonButton id={x.id} />}
      </div>
      <SpecRows idea={x.idea} />
    </div>
  )
}

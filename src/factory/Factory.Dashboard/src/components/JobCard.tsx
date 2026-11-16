import type { DragEvent, MouseEvent, ReactNode } from 'react'
import { useState } from 'react'
import { useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { Spinner } from '@/components/ui/spinner'
import { api, enc } from '@/lib/api'
import { blockedFromDeploy, BUILDING, elapsed, leaf, preview, ROLES, usageLimited, waitReason } from '@/lib/board'
import { cn } from '@/lib/cn'
import type { Job } from '@/lib/types'

/** A small action on a card: it never opens the card, and it is disabled once pressed. */
export function ChipButton({ onClick, children, className, ...data }: { onClick: () => unknown; children: ReactNode; className?: string } & Record<`data-${string}`, unknown>) {
  const [busy, setBusy] = useState(false)
  return (
    <button type="button" {...data} disabled={busy}
      className={cn('chip action cursor-pointer bg-surface-raised text-content hover:border-brand disabled:opacity-50', className)}
      onClick={(e) => { e.stopPropagation(); setBusy(true); Promise.resolve(onClick()).finally(() => setBusy(false)) }}>
      {children}
    </button>
  )
}

const stop = (e: MouseEvent) => e.stopPropagation()

/** The job's preview: its state, and Launch / QA login / Tear down while it is up. */
function PreviewBar({ j, now }: { j: Job; now: number }) {
  const { toast } = useUi()
  const d = j.deploy
  if (!d?.status) return null
  const { status, label, url, live } = preview(j, now)
  const title = label === 'destroy failed' ? d.destroyError : d.destroyAt ? `auto-destroys ${new Date(d.destroyAt).toLocaleString()}` : undefined
  const qaLogin = async () => {
    const r = await fetch(`/api/jobs/${enc(j.id)}/qa-login`)
    let res; try { res = await r.json() } catch { res = { ok: false, message: `HTTP ${r.status}` } }
    toast(res.ok ? res.message.replace(/\n/g, ' · ') : res.message, !res.ok)
  }
  const tearDown = async () => {
    const res = await api('POST', `/api/jobs/${enc(j.id)}/destroy`, {})
    toast(res.ok ? `${j.id}: ${res.message}` : res.message, !res.ok)
  }
  return (
    <div className="meta mt-1" data-preview-bar>
      <span className={cn('chip preview', status)} data-preview title={title}>preview: {label}</span>
      {live && url && <a className="chip action bg-surface-raised text-content" data-launch href={url} target="_blank" rel="noopener" draggable={false} onClick={stop}>Launch</a>}
      {live && <ChipButton data-qa onClick={qaLogin}>QA login</ChipButton>}
      {(live || status === 'failed') && <ChipButton data-teardown onClick={tearDown}>Tear down</ChipButton>}
    </div>
  )
}

/** A blocked card's ways out (contract, "Blocked"); a card stopped on a usage limit resumes instead. */
function BlockedBar({ j }: { j: Job }) {
  const { toast, move, openModal, refresh } = useUi()
  const post = async (what: 'resume' | 'redeploy', done: string) => {
    const res = await api('POST', `/api/jobs/${enc(j.id)}/${what}`, {})
    toast(res.ok ? `${j.id}: ${done}` : res.message, !res.ok)
    await refresh()
  }
  const resume = <ChipButton data-resume onClick={() => post('resume', 'resuming')}>Resume</ChipButton>
  if (usageLimited(j) && j.stage === 'ready') return <div className="meta mt-1" data-blocked-bar>{resume}</div>
  if (j.stage !== 'blocked') return null
  return (
    <div className="meta mt-1" data-blocked-bar>
      {usageLimited(j) ? resume : blockedFromDeploy(j) && <ChipButton data-redeploy onClick={() => post('redeploy', 'retrying deploy')}>Retry deploy</ChipButton>}
      <ChipButton data-rework onClick={() => openModal('job', j.id, { focusRework: true })}>Rework</ChipButton>
      <ChipButton data-discard onClick={() => move(j.id, 'discarded')}>Discard</ChipButton>
    </div>
  )
}

export function JobCard({ j, now, onDragStart, onDragEnd }: {
  j: Job; now: number; onDragStart: (j: Job, e: DragEvent) => void; onDragEnd: () => void
}) {
  const { board, canary, config } = useData()
  const { open, openModal, moving } = useUi()
  const isMoving = moving.has(j.id)
  const draggable = (j.stage === 'backlog' || j.stage === 'ready') && !isMoving
  const reviews = j.reviews && ROLES.some((r) => j.reviews![r] && j.reviews![r] !== 'pending')
  // Stacked on an unmerged job (contract, "Stacked jobs"): built on its branch, merged after it.
  const baseJob = j.base ? board.jobs.find((x) => x.id === j.base) : null
  return (
    <div className={cn('card', open?.id === j.id && 'selected')} data-card={j.id} data-stage={j.stage} draggable={draggable}
      onDragStart={(e) => onDragStart(j, e)} onDragEnd={onDragEnd}
      // The rest of a double-click on Approve lands on whatever the closed modal uncovered.
      onClick={(e) => { if (e.detail <= 1) openModal('job', j.id) }}>
      <div className="top"><span>{j.id}</span><span>r{j.round} · {elapsed(j, now)}</span></div>
      <div className="title">{j.title}</div>
      {j.target && <div className="repo" title={j.target}>→ {leaf(j.target)}</div>}
      <div className="meta">
        {isMoving && <span className="sub inline-flex items-center gap-1" data-moving><Spinner size="sm" label="Moving…" className="text-intent-running" />Moving…</span>}
        {j.kind === 'bootstrap' && <span className="chip kind" data-kind="bootstrap">bootstrap</span>}
        {baseJob && baseJob.stage !== 'done' && <span className="chip kind" data-base title={`built on ${j.base}'s unmerged branch; merges after it`}>on {j.base}</span>}
        {j.baseMoved?.by && (
          <span className="chip kind" data-base-moved title={`${j.baseMoved.by} merged after this spec was written; check the spec still fits main`}>base moved: {j.baseMoved.by} merged</span>
        )}
        {j.pr?.url && (j.stage === 'ready' || j.stage === 'blocked') && (
          <a className="chip pr" data-pr href={j.pr.url} target="_blank" rel="noopener" draggable={false} onClick={stop}>PR #{j.pr.number}</a>
        )}
        {BUILDING.includes(j.stage) || j.stage === 'blocked'
          ? <span className="sub">{j.stage} r{j.round}{j.stage === 'deploy' && j.deploy ? ` · ${j.deploy.attempts}/5` : ''}</span>
          : j.stage === 'approved' && <span className="sub" data-wait>{waitReason(j, board.jobs, canary, config.deploys)}</span>}
        {j.flag && <span className="flag" data-flag title={j.flag.detail}>{j.flag.reason}</span>}
        {reviews && ROLES.map((r) => <span key={r} className={cn('chip', j.reviews![r])} data-review={r} title={r}>{r}:{j.reviews![r]}</span>)}
        {j.costUsd != null && <span data-cost>${Number(j.costUsd).toFixed(2)}</span>}
      </div>
      <PreviewBar j={j} now={now} />
      <BlockedBar j={j} />
    </div>
  )
}

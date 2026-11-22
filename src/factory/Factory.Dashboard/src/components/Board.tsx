import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { DragEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { keys, useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { api } from '@/lib/api'
import { COLUMNS, DROP_TARGETS, dropAction } from '@/lib/board'
import { cn } from '@/lib/cn'
import type { Board as BoardData, Job } from '@/lib/types'
import { IdeaCard, SessionCard } from './IdeaCards'
import { JobCard } from './JobCard'

const TICK_MS = 15_000

/** FLIP: each card animates from where it was to where it is now. */
function useFlip(root: React.RefObject<HTMLElement | null>) {
  const was = useRef(new Map<string, DOMRect>())
  useLayoutEffect(() => {
    const now = new Map<string, DOMRect>()
    for (const el of root.current?.querySelectorAll<HTMLElement>('[data-card]') ?? []) {
      const r = el.getBoundingClientRect()
      now.set(el.dataset.card!, r)
      const before = was.current.get(el.dataset.card!)
      const dx = before ? before.left - r.left : 0, dy = before ? before.top - r.top : 0
      if (dx || dy) el.animate?.([{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'none' }], { duration: 450, easing: 'cubic-bezier(.2,.7,.2,1)' })
    }
    was.current = now
  })
}

export function Board() {
  const { board, boardReadAt, sessions, config } = useData()
  const { move, openModal, toast, refresh } = useUi()
  const qc = useQueryClient()
  const [tick, setTick] = useState(() => Date.now())
  const [dragging, setDragging] = useState<{ id: string; stage: string } | null>(null)
  const root = useRef<HTMLElement>(null)
  useFlip(root)
  // The cards' clocks run to the last board read, or to a 15 s tick between changes, as the old page re-rendered.
  useEffect(() => { const t = setInterval(() => setTick(Date.now()), TICK_MS); return () => clearInterval(t) }, [])
  const now = Math.max(tick, boardReadAt)

  const onDragStart = (j: Job, e: DragEvent) => {
    e.dataTransfer.setData('text/plain', j.id)
    e.dataTransfer.effectAllowed = 'move'
    setDragging({ id: j.id, stage: j.stage })
  }
  const onDrop = async (to: string, e: DragEvent) => {
    if (!dragging) return
    e.preventDefault()
    const { id, stage } = dragging
    setDragging(null)
    const action = dropAction(stage, to)
    if (action === 'rework') {
      openModal('job', id, { focusRework: true })
      toast('Add a rework note, then press Rework')
    } else if (action === 'move') await move(id, to)
  }

  const purge = async () => {
    const res = await api('POST', '/api/purge')
    await refresh()
    const fresh = qc.getQueryData<BoardData>(keys.board) ?? board
    const kept = fresh.jobs.filter((j) => j.stage === 'done' || j.stage === 'discarded').map((j) => j.id)
    const keptMsg = kept.length ? `${kept.join(', ')} kept until evaluated` : ''
    const cleared = res.message ? `Cleared ${res.message.split('\n').join(', ')}` : kept.length ? '' : 'Nothing to clear'
    toast(res.ok ? [cleared, keptMsg].filter(Boolean).join(' · ') : res.message, !res.ok)
  }

  const inSession = new Set(sessions.map((x) => x.idea).filter(Boolean))
  return (
    <main id="board" ref={root} className="grid grid-cols-[repeat(7,78vw)] items-start gap-3 overflow-x-auto p-4 md:grid-cols-[repeat(7,minmax(160px,1fr))]">
      {COLUMNS.map((c) => {
        const items = c.key === 'ideas'
          ? [...sessions.map((x) => <SessionCard key={x.id} x={x} />),
            ...board.ideas.filter((i) => i.status !== 'done' && i.status !== 'abandoned' && !inSession.has(i.id)).map((i) => <IdeaCard key={i.id} i={i} stage={board.jobs.find((j) => j.id === i.jobs?.[0])?.stage} />)]
          : board.jobs.filter((j) => c.stages!.includes(j.stage)).map((j) => (
            <JobCard key={j.id} j={j} now={now} onDragStart={onDragStart} onDragEnd={() => setDragging(null)} />
          ))
        const lit = dragging && (DROP_TARGETS[dragging.stage] ?? []).includes(c.key)
        return (
          <section key={c.key} data-column={c.key}
            className={cn('flex min-h-[200px] flex-col rounded-surface border border-border-subtle bg-surface-sunken',
              lit && 'drop-ok outline-2 -outline-offset-2 outline-brand outline-dashed')}
            onDragOver={(e) => { if (dragging) e.preventDefault() }} onDrop={(e) => onDrop(c.key, e)}>
            <h2 className="m-0 flex items-center justify-between px-3 py-2.5 text-[13px] font-semibold tracking-wide text-content-muted uppercase">
              {c.label}
              <span className="flex items-center gap-2">
                {items.length}
                {c.key === 'done' && (
                  <Button id="purge-btn" variant="secondary" size="sm" className="normal-case" onClick={purge}
                    title="Remove evaluated done and discarded cards, their folders and branches; unevaluated ones stay">Clear</Button>
                )}
                {c.key === 'ideas' && config.enabled && (
                  <Button id="new-idea-btn" size="sm" className="normal-case" onClick={() => openModal('new-session', null)}
                    title="Grill an idea with Claude, here">New idea</Button>
                )}
              </span>
            </h2>
            <div className="flex min-h-[120px] flex-1 flex-col gap-2 px-2 pb-2">{items}</div>
          </section>
        )
      })}
    </main>
  )
}

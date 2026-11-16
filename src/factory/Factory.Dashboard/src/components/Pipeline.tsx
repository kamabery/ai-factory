import { Fragment } from 'react'
import { useData } from '@/app/data'
import { activeAgents, NODES } from '@/lib/board'
import { cn } from '@/lib/cn'

/** The agents and the gate, in order: a node glows with the ids it is running for. */
export function Pipeline() {
  const { events, board } = useData()
  const active = activeAgents(events, board.jobs)
  const node = (n: string) => {
    const ids = active[n] ?? []
    return (
      <span key={n} data-node={n}
        className={cn('relative rounded-pill border px-3 py-1 whitespace-nowrap',
          ids.length ? 'active border-intent-running bg-intent-running-subtle text-intent-running [animation:glow_1.2s_ease-in-out_infinite]'
            : 'border-border-subtle bg-surface-sunken text-content-muted')}>
        {n}
        {ids.length > 0 && (
          <span className="ml-1.5 inline-flex gap-0.5">
            {ids.map((i) => <span key={i} className="rounded-control bg-intent-running px-1 font-mono text-[11px] text-intent-running-contrast">{i}</span>)}
          </span>
        )}
      </span>
    )
  }
  return (
    <nav id="pipeline" aria-label="Pipeline" className="flex items-center gap-1.5 overflow-x-auto border-b border-border-subtle bg-surface-raised px-4 py-2.5">
      {NODES.map((n, i) => (
        <Fragment key={i}>
          {i > 0 && <span className="text-content-muted">→</span>}
          {Array.isArray(n)
            ? <span className="inline-flex gap-1 rounded-pill border border-dashed border-border-subtle p-0.5">{n.map(node)}</span>
            : node(n)}
        </Fragment>
      ))}
    </nav>
  )
}

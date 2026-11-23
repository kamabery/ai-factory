import { useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { ChipButton } from '@/components/JobCard'
import { Spinner } from '@/components/ui/spinner'
import { leaf } from '@/lib/board'
import type { Job } from '@/lib/types'

/** The spec's Approve (the `approve` the drag to Approved ran), Edit (its opened card) and its sticky refusal. */
function SpecActions({ j, discard }: { j: Job; discard?: boolean }) {
  const { move, moving, openModal, refusals } = useUi()
  const { board } = useData()
  const refused = refusals.get(j.id)
  const baseJob = j.base ? board.jobs.find((x) => x.id === j.base) : null
  return (
    <>
      <div className="meta">
        {moving.has(j.id)
          ? <span className="chip inline-flex items-center gap-1.5" data-spec-approve><Spinner />Approving…</span>
          : <ChipButton data-spec-approve onClick={() => move(j.id, 'approved')}>Approve</ChipButton>}
        <ChipButton data-spec-edit onClick={() => openModal('job', j.id)}>Edit</ChipButton>
        {discard && <ChipButton data-discard onClick={() => move(j.id, 'discarded')}>Discard</ChipButton>}
        {baseJob && baseJob.stage !== 'done' && <span className="chip kind" data-base title={`built on ${j.base}'s unmerged branch; merges after it`}>on {j.base}</span>}
        {j.baseMoved?.by && <span className="chip kind" data-base-moved title={`${j.baseMoved.by} merged after this spec was written; check the spec still fits main`}>base moved: {j.baseMoved.by} merged</span>}
      </div>
      {refused && <div className="sub break-words text-intent-danger" data-refused>{refused}</div>}
    </>
  )
}

/** An idea's specs still in backlog, one row each, on its idea or session card (contract, "Board columns and stages"). */
export function SpecRows({ idea }: { idea?: string | null }) {
  const { board } = useData()
  const specs = idea ? board.jobs.filter((j) => j.stage === 'backlog' && j.idea === idea) : []
  if (specs.length === 0) return null
  return (
    <div className="mt-1 flex flex-col gap-1.5 border-t border-border-subtle pt-1.5" onClick={(e) => e.stopPropagation()}>
      {specs.map((j) => (
        <div key={j.id} data-spec={j.id}>
          <div className="text-[12.5px]"><span className="text-content-muted">{j.id}</span> {j.title}</div>
          <SpecActions j={j} />
        </div>
      ))}
    </div>
  )
}

/** A backlog spec whose idea is not shown in Ideas: its own card, with Approve, Edit and Discard. */
export function SpecCard({ j }: { j: Job }) {
  const { openModal } = useUi()
  return (
    <div className="card idea-card border-l-[3px] border-l-border-strong" data-card={j.id} data-spec-card data-stage={j.stage} onClick={() => openModal('job', j.id)}>
      <div className="top"><span>{j.id}</span><span>spec</span></div>
      <div className="title !font-medium">{j.title}</div>
      {j.target && <div className="repo" title={j.target}>→ {leaf(j.target)}</div>}
      <div onClick={(e) => e.stopPropagation()}><SpecActions j={j} discard /></div>
    </div>
  )
}

import { Fragment, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { getText } from '@/lib/api'
import { cn } from '@/lib/cn'
import { md } from '@/lib/markdown'
import { phaseSteps, stageText, waitingText, type Phase } from '@/lib/session'
import { Actions, Md } from './Modal'
import { RepoActions } from './RepoActions'

type Session = { status: string; target?: string; bootstrap?: string | null; idea?: string | null; kind?: string | null; pending?: { name: string } | null; specs?: unknown[] }

const link = 'inline-flex items-center rounded-control border border-border-subtle bg-surface-raised px-3.5 py-2 text-sm font-medium text-content hover:border-brand'

/**
 * A new-repo session's phases, Brief → Scaffold (J-NNN) → Specs → Build, with the current one lit and what it waits
 * on in plain text (contract, "Idea sessions (dashboard)", "The phase"). The scaffold is merged here or on the board;
 * the specs then start with no click.
 */
export function SessionStepper({ s, phase }: { s: Session; phase: Phase }) {
  const { move } = useUi()
  const [merging, setMerging] = useState(false)
  const steps = phaseSteps(phase, s.bootstrap, s.kind === 'repo')
  const toMerge = (phase.name === 'scaffold' && phase.stage === 'ready') || phase.name === 'review'
  const merge = async () => {
    setMerging(true)
    await move(phase.job!, 'done')
    setMerging(false)
  }
  return (
    <div data-stepper data-phase={phase.name} className="mb-2.5 rounded-surface border border-border-subtle px-3 py-2.5">
      <ol className="m-0 flex list-none flex-wrap items-center gap-1.5 p-0 text-sm">
        {steps.map((st, i) => (
          <Fragment key={st.key}>
            {i > 0 && <span aria-hidden className="text-content-muted">→</span>}
            <li data-step={st.key} data-state={st.state} aria-current={st.state === 'now' ? 'step' : undefined}
              className={cn('rounded-control px-2 py-0.5',
                st.state === 'now' ? 'bg-brand font-semibold text-brand-contrast' : st.state === 'done' ? 'text-content' : 'text-content-muted')}>
              {st.label}
            </li>
          </Fragment>
        ))}
      </ol>
      <p data-waiting className="mt-2 mb-0">{waitingText(phase, s)}</p>
      {toMerge && (
        <>
          <Actions>
            {phase.pr && <a id="s-boot-pr" className={link} href={phase.pr.url} target="_blank" rel="noopener">Open PR #{phase.pr.number}</a>}
            <Button id="s-merge-boot" disabled={merging} onClick={merge}>{merging ? 'Merging…' : 'Merge'}</Button>
          </Actions>
          <p className="mt-1.5 mb-0 text-xs text-content-muted">
            Merged it on GitHub instead? The dashboard records that at its next PR sync, while it is open, and {s.kind === 'repo' ? 'its features can start then' : 'the specs start then'}.
          </p>
        </>
      )}
      {(phase.name === 'scaffold' || phase.name === 'review') && phase.job && <BootSpec job={phase.job} stage={phase.stage} />}
      {phase.name === 'merged' && s.idea && <RepoActions idea={s.idea} />}
      {phase.name === 'build' && (
        <ul className="mt-2 mb-0 pl-4" data-build>
          {(phase.jobs ?? []).map((j) => (
            <li key={j.id} data-build-job={j.id}>
              <b>{j.id}</b> {j.title} <span className="text-content-muted">{stageText(j.stage)}</span>
              {j.pr?.url && <> · <a href={j.pr.url} target="_blank" rel="noopener" className="text-brand">PR #{j.pr.number}</a></>}
              {j.deploy?.status === 'up' && j.deploy.urls?.frontend && <> · <a href={j.deploy.urls.frontend} target="_blank" rel="noopener" className="text-brand">preview</a></>}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

function BootSpec({ job, stage }: { job: string; stage?: string }) {
  const spec = useQuery({ queryKey: ['text', `/state/jobs/${job}/spec.md`, stage], queryFn: () => getText(`/state/jobs/${job}/spec.md`) }).data
  return (
    <details className="mt-2">
      <summary className="cursor-pointer font-semibold">Bootstrap spec</summary>
      <Md html={spec === undefined ? '…' : md(spec)} data-boot-spec />
    </details>
  )
}

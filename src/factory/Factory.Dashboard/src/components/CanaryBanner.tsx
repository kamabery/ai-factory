import { useState } from 'react'
import { useCanaryDue, useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { api } from '@/lib/api'
import { cn } from '@/lib/cn'
import type { Canary } from '@/lib/types'

const PHASES: Record<string, string> = { scaffold: 'scaffolding', deploy: 'deploying', smoke: 'smoke check', signin: 'sign-in check' }

/** `deploying 2/3 · 7 of ~15 min` (contract, "Canary", "Phases"): the banner and a Canary card both show it. */
export function canaryProgress(c: Canary, now: number, typical: number) {
  let phase = c.phase ? PHASES[c.phase] ?? c.phase : 'running'
  if (c.phase === 'deploy' && c.deploy && c.deploy.total > 0) phase += ` ${Math.min(c.deploy.done + 1, c.deploy.total)}/${c.deploy.total}`
  const started = Date.parse(c.startedAt ?? '')
  const minutes = Number.isNaN(started) ? 0 : Math.max(0, Math.floor((now - started) / 60_000))
  return `${phase} · ${minutes} of ~${typical} min`
}

/** `Deploy canary (0.4.2) · deploying 2/3 · 7 of ~15 min`. */
export function running(c: Canary, now: number, typical: number) {
  return `${c.version ? `Deploy canary (${c.version})` : 'Deploy canary'} · ${canaryProgress(c, now, typical)}`
}

/**
 * The deploy canary (contract, "Canary"): a banner while one runs, after one failed (deployable jobs wait in
 * Canary until it passes), or after a pass whose j000 teardown failed.
 */
export function CanaryBanner() {
  const { canary: c, boardReadAt, board } = useData()
  const due = useCanaryDue()
  const waiting = board.jobs.filter((j) => j.stage === 'canary').map((j) => j.id)
  const { toast } = useUi()
  const [sent, setSent] = useState<string | null>(null)
  const stuck = c?.status === 'pass' && c.teardown?.status === 'failed'
  if (!c || (c.status !== 'failed' && c.status !== 'running' && !stuck)) return null

  const log = (
    <a data-canary-log href={`/state/${c.log}`} target="_blank" rel="noopener" className="underline">Log</a>
  )
  const run = async () => {
    setSent(c.log ?? '')
    const r = await api('POST', '/api/canary', {})
    toast(r.message, !r.ok)
  }
  return (
    <div id="canary" role="status"
      className={cn('flex flex-wrap items-center gap-3 border-b border-border-subtle px-4 py-2 text-[13px]',
        c.status === 'running' ? 'running bg-intent-running-subtle text-intent-running' : 'bg-intent-danger-subtle text-intent-danger')}>
      {c.status === 'running' ? (
        <>
          <span className="min-w-0 flex-1">{running(c, boardReadAt || Date.now(), due?.typicalMinutes ?? 15)}: deployable jobs wait for it.</span>
          {waiting.length > 0 && <span data-canary-waiting>waiting in Canary: {waiting.join(', ')}</span>}
          {c.run && <a data-canary-run href={c.run} target="_blank" rel="noopener" className="underline">Actions run ↗</a>}
        </>
      ) : stuck ? (
        <>
          <span className="min-w-0 flex-1 break-words">
            <strong>The deploy canary passed, but j000 could not be torn down</strong> ({c.teardown?.failures} of 5 tries). <code>{c.teardown?.error}</code>
          </span>
          {log}
        </>
      ) : (
        <>
          <span className="min-w-0 flex-1 break-words">
            <strong>Deployable jobs are waiting:</strong> the deploy canary failed. <code>{c.error}</code>
          </span>
          {log}
          <Button type="button" variant="secondary" size="sm" data-run-canary disabled={sent === (c.log ?? '')} onClick={run}>Run canary</Button>
        </>
      )}
    </div>
  )
}

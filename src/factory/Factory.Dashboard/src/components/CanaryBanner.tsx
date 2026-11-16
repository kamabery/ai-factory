import { useState } from 'react'
import { useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { api } from '@/lib/api'
import { cn } from '@/lib/cn'

/**
 * The deploy canary (contract, "Canary"): a banner while one runs, after one failed (deployable jobs wait in
 * Approved until it passes), or after a pass whose j000 teardown failed.
 */
export function CanaryBanner() {
  const { canary: c } = useData()
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
        <span className="min-w-0 flex-1">Deploy canary running (since {new Date(c.startedAt ?? '').toLocaleTimeString()}): deployable jobs wait for it.</span>
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

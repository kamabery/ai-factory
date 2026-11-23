import { useState } from 'react'
import { usePlatform } from '@/app/data'
import { useUi } from '@/app/ui'
import { api } from '@/lib/api'

/**
 * The preview platform's Postgres server (contract, "The platform", spec 24): `platform: awake` with Sleep, `asleep`
 * with Start, and the moving marker while starting or stopping. A refused Sleep names what holds it and stays.
 */
export function PlatformChip() {
  const status = usePlatform()
  const { toast } = useUi()
  // What was asked for, shown until the status reaches its end: the server's answer is up to 30 s old.
  const [asked, setAsked] = useState<{ word: 'starting' | 'stopping'; from: string } | null>(null)
  if (!status) return null
  const word = asked && status === asked.from ? asked.word : status
  const send = async (verb: 'start' | 'sleep', next: 'starting' | 'stopping') => {
    const r = await api('POST', `/api/platform/${verb}`, {})
    if (r.ok) setAsked({ word: next, from: status })
    else toast(r.message, true)
  }
  const moving = word === 'starting' || word === 'stopping'
  const button = 'rounded-control border border-border-subtle bg-surface-sunken px-2 py-0.5 text-xs text-content'
  return (
    <span id="platform" data-platform={word} title="The preview platform's Postgres server: about $13 a month while it runs"
      className="mt-1 inline-flex items-center gap-1.5 text-xs whitespace-nowrap text-content-muted">
      {moving && <span data-spinner aria-hidden className="inline-block size-2 shrink-0 animate-pulse rounded-full bg-intent-running" />}
      platform: {word}
      {word === 'asleep' && <button type="button" data-platform-start className={button} onClick={() => send('start', 'starting')}>Start</button>}
      {word === 'awake' && <button type="button" data-platform-sleep className={button} onClick={() => send('sleep', 'stopping')}>Sleep</button>}
    </span>
  )
}

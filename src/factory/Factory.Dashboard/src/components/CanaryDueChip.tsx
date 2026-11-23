import { useCanaryDue } from '@/app/data'

/** "canary due" by the run-mode chip (contract, "Canary", "Why it is due"): quiet until the fingerprint has no pass. */
export function CanaryDueChip() {
  const d = useCanaryDue()
  if (!d?.due) return null
  const why = (d.reasons ?? []).join('; ')
  return (
    <span id="canary-due" data-canary-due title="A deploy canary has to pass for these inputs before a deployable job builds"
      className="mt-1.5 text-xs text-content-muted">
      canary due{why ? `: ${why}` : ''} · the next deployable job waits in Canary for it (~{d.typicalMinutes ?? 15} min)
    </span>
  )
}

import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { api } from '@/lib/api'
import { cn } from '@/lib/cn'
import { phaseSteps, type Phase } from '@/lib/session'
import type { Product, Result } from '@/lib/types'

const productKey = (idea: string) => ['product', idea]

/** GET /api/ideas/<I>/product answers the product, or { ok: false, message } when PRODUCT.md cannot be read. */
async function readProduct(idea: string): Promise<Product | Result> {
  const r = await fetch(`/api/ideas/${idea}/product`, { cache: 'no-store' })
  try { return (await r.json()) as Product | Result } catch { return { ok: false, message: `HTTP ${r.status}` } }
}

/** The New repo chip and its four steps, the current one lit (contract, "The phase"). */
export function RepoSteps({ phase }: { phase: Phase }) {
  return (
    <div className="meta">
      <span data-repo-chip className="chip kind border-brand text-brand">New repo</span>
      {phaseSteps(phase, null, true).map((st) => (
        <span key={st.key} data-repo-step data-state={st.state}
          className={cn('text-xs', st.state === 'now' ? 'font-semibold text-brand' : st.state === 'done' ? 'text-content' : 'text-content-muted')}>
          {st.label}
        </span>
      ))}
    </div>
  )
}

/**
 * A merged repo's next move (spec 22): Implement its next feature in a session of its own, or Dismiss. While a session
 * works on a feature, the card shows it instead; a PRODUCT.md that cannot be read leaves Dismiss only.
 */
export function RepoActions({ idea }: { idea: string }) {
  const { sessions, board } = useData()
  const { openModal, toast, refresh } = useUi()
  const qc = useQueryClient()
  const [busy, setBusy] = useState(false)
  // Read again when a feature starts (its idea is recorded on the board), not only when the 60 s are up.
  const started = board.ideas.find((i) => i.id === idea)?.features?.length ?? 0
  const product = useQuery({ queryKey: [...productKey(idea), started], queryFn: () => readProduct(idea), staleTime: 60_000 }).data
  const open = sessions.find((x) => x.from === idea)
  const stop = (e: React.MouseEvent) => e.stopPropagation()
  const write = async (action: 'implement' | 'dismiss') => {
    setBusy(true)
    const r = await api('POST', `/api/ideas/${idea}/${action}`, {})
    await qc.invalidateQueries({ queryKey: productKey(idea) })
    await refresh()
    setBusy(false)
    if (!r.ok) { toast(r.message || 'refused', true); return }
    if (action === 'implement') openModal('session', r.message)
    else toast(`${idea} dismissed`)
  }
  const refused = product && 'ok' in product && !product.ok ? product.message : null
  const p = product && 'features' in product ? product : null
  const next = p?.features.find((f) => f.k === p.next)
  const first = !!p && p.features.every((f) => !f.idea)
  return (
    <div data-repo-actions className="meta" onClick={stop}>
      {open ? (
        <button type="button" data-feature-open className="chip action cursor-pointer bg-surface-raised text-content" onClick={() => openModal('session', open.id)}>
          {open.id} is working on {open.feature}
        </button>
      ) : next ? (
        <>
          <button type="button" data-implement disabled={busy} className="chip action cursor-pointer border-brand bg-brand font-semibold text-brand-contrast"
            onClick={() => write('implement')}>
            {first ? 'Implement first feature' : `Implement ${next.heading}`}
          </button>
          <span data-next-feature className="text-xs text-content-muted">{next.heading}</span>
        </>
      ) : refused ? <span data-product-error className="text-xs text-intent-danger">{refused.replace(/^product refused: /, '')}</span>
        : null}
      <button type="button" data-dismiss disabled={busy} className="chip action cursor-pointer bg-surface-raised text-content" onClick={() => write('dismiss')}>Dismiss</button>
    </div>
  )
}

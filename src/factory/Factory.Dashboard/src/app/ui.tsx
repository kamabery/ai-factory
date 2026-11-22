// What the old page kept in its global `state` besides the polled files: the open card, the toast, the cards with
// an approve in flight, the armed Abandon, the view. Every write goes through `api` and then refreshes the polls.
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { COLUMNS } from '@/lib/board'
import { keys } from './data'

export type OpenKind = 'job' | 'idea' | 'session' | 'new-session'
export type Open = { kind: OpenKind; id: string | null; focusRework?: boolean; n: number }
export type View = 'board' | 'evals' | 'lessons'
export type ToastState = { text: string; error: boolean; sticky: boolean; n: number } | null

type Ui = {
  view: View
  setView: (v: View) => void
  open: Open | null
  openModal: (kind: OpenKind, id: string | null, opts?: { focusRework?: boolean }) => void
  /** Asks first while the spec has unsaved edits; `force` skips that, for a close that follows a write. */
  closeModal: (force?: boolean) => void
  /** Whether the open job's spec has unsaved edits; its editor sets it. */
  isDirty: () => boolean
  setDirty: (on: boolean) => void
  toastState: ToastState
  /** An error (a refusal) stays until dismissed; so does a `sticky` message. Anything else fades after 4 s. */
  toast: (text: string, error?: boolean, opts?: { sticky?: boolean }) => void
  dismissToast: () => void
  moving: ReadonlySet<string>
  /** A refused approval per job, kept until its next try succeeds (contract, "Board columns and stages"). */
  refusals: ReadonlyMap<string, string>
  move: (id: string, to: string, note?: string) => Promise<boolean>
  refresh: () => Promise<void>
  armed: string | null
  abandon: (key: string, now?: boolean) => Promise<void>
}

const UiContext = createContext<Ui | null>(null)

export function useUi(): Ui {
  const ui = useContext(UiContext)
  if (!ui) throw new Error('useUi must be used inside UiProvider.')
  return ui
}

const TOAST_MS = 4000
const ARM_MS = 4000
let opened = 0

export function UiProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient()
  const [view, setView] = useState<View>('board')
  const [open, setOpen] = useState<Open | null>(null)
  const [toastState, setToast] = useState<ToastState>(null)
  const [moving, setMoving] = useState<ReadonlySet<string>>(new Set())
  const [refusals, setRefusals] = useState<ReadonlyMap<string, string>>(new Map())
  const [armed, setArmed] = useState<string | null>(null)
  const dirty = useRef(false)
  const toastTimer = useRef<ReturnType<typeof setTimeout>>(undefined)
  const armTimer = useRef<ReturnType<typeof setTimeout>>(undefined)
  const movingNow = useRef(new Set<string>())
  const openRef = useRef<Open | null>(null)
  useEffect(() => { openRef.current = open }, [open])

  const toast = useCallback((text: string, error = false, opts: { sticky?: boolean } = {}) => {
    const sticky = error || !!opts.sticky
    setToast({ text, error, sticky, n: ++opened })
    clearTimeout(toastTimer.current)
    if (!sticky) toastTimer.current = setTimeout(() => setToast(null), TOAST_MS)
  }, [])

  const dismissToast = useCallback(() => { clearTimeout(toastTimer.current); setToast(null) }, [])

  const refresh = useCallback(async () => {
    await Promise.all([keys.board, keys.events, keys.sessions, keys.canary].map((queryKey) => qc.invalidateQueries({ queryKey })))
  }, [qc])

  const openModal = useCallback((kind: OpenKind, id: string | null, opts: { focusRework?: boolean } = {}) => {
    if (openRef.current?.id !== id) dirty.current = false
    setOpen({ kind, id, focusRework: opts.focusRework, n: ++opened })
  }, [])

  const closeModal = useCallback((force = false) => {
    if (!force && dirty.current && !confirm('Discard unsaved spec edits?')) return
    dirty.current = false
    setOpen(null)
  }, [])

  const setMove = (id: string, on: boolean) => {
    if (on) movingNow.current.add(id); else movingNow.current.delete(id)
    setMoving(new Set(movingNow.current))
  }

  const move = useCallback(async (id: string, to: string, note?: string) => {
    if (to === 'approved') {
      if (movingNow.current.has(id)) return false   // already on its way; a second request would be refused
      setMove(id, true)
    }
    if (to === 'done') toast(`${id}: merging…`)
    const res = await api('POST', `/api/jobs/${id}/move`, note ? { to, note } : { to })
    if (to === 'approved') {
      setMove(id, false)
      setRefusals((m) => { const n = new Map(m); if (res.ok) n.delete(id); else n.set(id, res.message || 'refused'); return n })
    }
    if (res.ok) toast(`${id} → ${to === 'discarded' ? 'discarded' : COLUMNS.find((c) => c.key === to)?.label ?? to}`)
    else toast(res.message || 'refused', true)
    await refresh()
    return res.ok
  }, [toast, refresh])

  // An Ideas card's Abandon asks first: a second click within 4 s abandons. In the opened idea or session it
  // abandons at once (`now`), opening it having been the confirmation.
  const armedRef = useRef<string | null>(null)
  const abandon = useCallback(async (key: string, now = false) => {
    clearTimeout(armTimer.current)
    if (!now && armedRef.current !== key) {
      armedRef.current = key
      setArmed(key)
      armTimer.current = setTimeout(() => { armedRef.current = null; setArmed(null) }, ARM_MS)
      return
    }
    armedRef.current = null
    setArmed(null)
    const r = key.startsWith('S-')
      ? await api('POST', `/api/sessions/${key}/close`, { abandon: true })
      : await api('POST', `/api/ideas/${key}/abandon`, {})
    toast(r.ok ? `${key} abandoned` : r.message, !r.ok)
    const o = openRef.current
    if (r.ok && (o?.kind === 'idea' || o?.kind === 'session') && o.id === key) setOpen(null)
    await refresh()
  }, [toast, refresh])

  const isDirty = useCallback(() => dirty.current, [])
  const setDirty = useCallback((on: boolean) => { dirty.current = on }, [])

  const value = useMemo<Ui>(() => ({
    view, setView, open, openModal, closeModal, isDirty, setDirty, toastState, toast, dismissToast, moving, refusals, move, refresh, armed, abandon,
  }), [view, open, openModal, closeModal, isDirty, setDirty, toastState, toast, dismissToast, moving, refusals, move, refresh, armed, abandon])

  return <UiContext.Provider value={value}>{children}</UiContext.Provider>
}

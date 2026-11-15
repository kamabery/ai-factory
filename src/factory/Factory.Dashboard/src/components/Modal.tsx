import { useEffect } from 'react'
import type { ReactNode } from 'react'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/cn'
import { IdeaModal } from './IdeaModal'
import { JobModal } from './JobModal'
import { NewSession } from './NewSession'
import { SessionModal } from './SessionModal'

/**
 * The card's details. A div over a backdrop rather than the template's native <dialog>: showModal() would make
 * the board inert and put the toast behind its backdrop, and a card's details are often read beside a toast.
 * Esc and a backdrop click close it, as the template's dialog does.
 */
export function Modal() {
  const { open, closeModal } = useUi()
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') closeModal() }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [open, closeModal])

  const key = open ? `${open.kind}:${open.id}:${open.kind === 'job' ? open.n : ''}` : ''
  return (
    <div id="backdrop" className={cn('fixed inset-0 z-10 items-center justify-center bg-scrim px-[2.5vw] py-[2.5vh]', open ? 'open flex' : 'hidden')}
      onClick={(e) => { if (e.target === e.currentTarget && e.detail <= 1) closeModal() }}>
      {open && (
        <section id="modal" role="dialog" aria-modal="true" aria-label="Details"
          className="flex h-[95vh] w-[min(1200px,95vw)] flex-col overflow-hidden rounded-surface border border-border-subtle bg-surface-raised text-content shadow-overlay md:h-[90vh]">
          {open.kind === 'job' && <JobModal key={key} id={open.id!} focusRework={!!open.focusRework} />}
          {open.kind === 'idea' && <IdeaModal key={key} id={open.id!} />}
          {open.kind === 'session' && <SessionModal key={key} id={open.id!} />}
          {open.kind === 'new-session' && <NewSession key={key} />}
        </section>
      )}
    </div>
  )
}

export function CloseButton() {
  const { closeModal } = useUi()
  return <Button variant="ghost" size="sm" className="close float-right" aria-label="Close" onClick={() => closeModal()}>✕</Button>
}

export function Head({ children }: { children: ReactNode }) {
  return <div id="m-head" className="border-b border-border-subtle px-4 pt-3.5 pb-2.5">{children}</div>
}

export function Title({ children }: { children: ReactNode }) {
  return <h3 className="m-0 mb-1 pr-10 text-lg font-semibold">{children}</h3>
}

export function SubMeta({ children }: { children: ReactNode }) {
  return <div className="sub-meta text-[12.5px] text-content-muted [&_code]:text-xs">{children}</div>
}

export function FlagBox({ children, ...data }: { children: ReactNode } & Record<`data-${string}`, unknown>) {
  return <div className="flagbox mt-2 rounded-control bg-intent-danger-subtle px-2.5 py-2 text-intent-danger" {...data}>{children}</div>
}

export function Actions({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn('actions mt-2 flex flex-wrap items-center gap-2', className)}>{children}</div>
}

/** The modal's tabs: buttons with data-tab, the selected one `on`. */
export function Tabs({ tabs, tab, onTab }: { tabs: string[]; tab: string; onTab: (t: string) => void }) {
  return (
    <nav id="m-tabs" role="tablist" className="flex gap-0.5 border-b border-border-subtle bg-surface-raised px-4">
      {tabs.map((t) => (
        <button key={t} type="button" role="tab" aria-selected={t === tab} data-tab={t} onClick={() => onTab(t)}
          className={cn('-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors duration-[var(--motion-fast)]',
            t === tab ? 'on border-brand text-content' : 'border-transparent text-content-muted hover:text-content')}>
          {t[0].toUpperCase() + t.slice(1)}
        </button>
      ))}
    </nav>
  )
}

export function Body({ children }: { children: ReactNode }) {
  return <div id="m-body" className="min-h-0 flex-1 overflow-auto px-4 py-3.5">{children}</div>
}

/** Rendered markdown (lib/markdown escapes raw HTML). */
export function Md({ html, className, id, ...data }: { html: string; className?: string; id?: string } & Record<`data-${string}`, unknown>) {
  return <div id={id} className={cn('md', className)} {...data} dangerouslySetInnerHTML={{ __html: html }} />
}

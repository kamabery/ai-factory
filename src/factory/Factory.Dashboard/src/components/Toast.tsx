import { useUi } from '@/app/ui'
import { cn } from '@/lib/cn'

/**
 * One message at a time, the latest replacing the last, as the old page did; the region stays in the DOM so a
 * screen reader announces each one. Styled as the template's toast. A refusal is larger and stays until dismissed,
 * so it is not missed (a rework refused for an empty note once went unseen); so does a session notice.
 */
export function Toast() {
  const { toastState: t, dismissToast } = useUi()
  return (
    <div id="toast" role="status" aria-live="polite" data-sticky={t?.sticky ? 1 : undefined}
      className={cn('fixed left-1/2 z-50 flex max-w-[min(90vw,40rem)] -translate-x-1/2 items-start gap-3 rounded-surface border border-l-4 border-border-subtle bg-surface-raised text-content shadow-overlay transition-opacity duration-[var(--motion-slow)]',
        t ? 'show opacity-100' : 'pointer-events-none opacity-0',
        t?.sticky ? 'bottom-8 px-5 py-3 text-base' : 'pointer-events-none bottom-5 px-3.5 py-2 text-sm',
        t?.error ? 'error border-l-intent-danger font-medium' : 'border-l-intent-info')}>
      <span className="whitespace-pre-wrap">{t?.text}</span>
      {t?.sticky && (
        <button type="button" id="toast-close" aria-label="Dismiss" onClick={dismissToast}
          className="-mr-1 cursor-pointer rounded-control px-1.5 leading-none text-content-muted hover:text-content">×</button>
      )}
    </div>
  )
}

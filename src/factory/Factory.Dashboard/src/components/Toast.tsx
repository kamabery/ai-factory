import { useUi } from '@/app/ui'
import { cn } from '@/lib/cn'

/**
 * One message at a time, the latest replacing the last, as the old page did; the region stays in the DOM so a
 * screen reader announces each one. Styled as the template's toast.
 */
export function Toast() {
  const { toastState: t } = useUi()
  return (
    <div id="toast" role="status" aria-live="polite"
      className={cn('pointer-events-none fixed bottom-5 left-1/2 z-50 max-w-[min(90vw,40rem)] -translate-x-1/2 rounded-surface border border-l-4 border-border-subtle bg-surface-raised px-3.5 py-2 text-sm text-content shadow-overlay transition-opacity duration-[var(--motion-slow)]',
        t ? 'show opacity-100' : 'opacity-0',
        t?.error ? 'error border-l-intent-danger' : 'border-l-intent-info')}>
      {t?.text}
    </div>
  )
}

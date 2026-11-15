import { cn } from '@/lib/cn'

/**
 * The template's shared look of every text-shaped control (aspire-ui-react, src/components/ui/input.tsx). The dashboard
 * uses native inputs, selects and textareas with this class rather than the template's Field-wired components.
 */
export const control = cn(
  'w-full rounded-control border border-border-subtle bg-surface-raised px-3 py-2 text-sm text-content hover:border-border-strong',
  'placeholder:text-content-muted',
  'transition-colors duration-[var(--motion-fast)]',
  'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-focus-ring',
  'disabled:cursor-not-allowed disabled:bg-surface-sunken disabled:text-content-disabled',
)

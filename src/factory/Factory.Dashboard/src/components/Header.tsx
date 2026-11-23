import { useConfig } from '@/app/data'
import { CanaryDueChip } from '@/components/CanaryDueChip'
import { PlatformChip } from '@/components/PlatformChip'
import { useUi, type View } from '@/app/ui'
import { cn } from '@/lib/cn'
import { modeChip, toggleTheme } from '@/lib/theme'

const VIEWS: [View, string][] = [['board', 'Board'], ['evals', 'Evaluations'], ['lessons', 'Lessons']]

export function Header() {
  const { view, setView } = useUi()
  const { config, loaded } = useConfig()
  const chip = loaded ? modeChip(config) : null
  return (
    <header className="flex flex-wrap items-start gap-3 border-b border-border-subtle bg-surface-raised px-4 py-3">
      <h1 className="mt-1.5 mr-2 text-base font-semibold whitespace-nowrap">Factory</h1>
      <nav id="views" aria-label="Views" className="mt-0.5 flex gap-0.5">
        {VIEWS.map(([v, label]) => (
          <button key={v} type="button" data-view={v} onClick={() => setView(v)} aria-current={view === v || undefined}
            className={cn('-mb-px border-b-2 px-2.5 py-1 text-sm font-medium transition-colors duration-[var(--motion-fast)]',
              view === v ? 'on border-brand text-content' : 'border-transparent text-content-muted hover:text-content')}>
            {label}
          </button>
        ))}
      </nav>
      <span id="idea-hint" className="mt-1.5 flex-1 text-[12.5px] text-content-muted">
        New idea: <code>factory idea &lt;owner/name&gt;</code> opens a Claude session that grills you, writes the specs and
        puts the ones you approve on the board.
      </span>
      <CanaryDueChip />
      <PlatformChip />
      <span id="mode" data-mode={chip?.mode} title={chip?.title ?? 'Run mode and the provider it decides (FACTORY_MODE)'}
        className={cn('mt-1.5 text-xs whitespace-nowrap',
          chip?.mode === 'cloud' ? 'mt-0.5 rounded-pill bg-intent-running-subtle px-2.5 py-1 font-semibold text-intent-running'
            : chip?.mode === 'invalid' ? 'font-semibold text-intent-danger' : 'text-content-muted')}>
        {chip?.text}
      </span>
      <button id="theme" type="button" title="Toggle theme" onClick={toggleTheme}
        className="rounded-control border border-border-subtle bg-surface-sunken px-3 py-1.5 text-content">◐</button>
    </header>
  )
}

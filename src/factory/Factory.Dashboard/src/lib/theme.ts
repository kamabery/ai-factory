import type { Config } from './types'

/** The run mode chip: cloud bills Microsoft Foundry per token, so it must not read like the quiet local default. */
export function modeChip(c: Config) {
  if (!c.mode) return { text: 'FACTORY_MODE is invalid', mode: 'invalid', title: 'FACTORY_MODE must be local or cloud' }
  return {
    text: `${c.mode} · ${c.provider}`,
    mode: c.mode,
    title: c.mode === 'cloud'
      ? 'Cloud mode: jobs and idea turns bill per token to Microsoft Foundry (FACTORY_MODE=cloud)'
      : 'Local mode: jobs run on your Claude subscription (FACTORY_MODE)',
  }
}

const THEME_KEY = 'factory-theme'

export function applySavedTheme() {
  try { const t = localStorage.getItem(THEME_KEY); if (t) document.documentElement.dataset.theme = t } catch { /* private window */ }
}

export function toggleTheme() {
  const root = document.documentElement
  const dark = root.dataset.theme ? root.dataset.theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches
  root.dataset.theme = dark ? 'light' : 'dark'
  try { localStorage.setItem(THEME_KEY, root.dataset.theme) } catch { /* private window */ }
}

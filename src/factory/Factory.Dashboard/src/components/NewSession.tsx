import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useConfig, useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { control } from '@/components/ui/control'
import { api, getJson } from '@/lib/api'
import type { Result } from '@/lib/types'
import { Actions, Body, CloseButton, Head, SubMeta, Title } from './Modal'

const NEW = '+new'
const OTHER = '+other'

/**
 * A new repo tells the human twice when it needs them (contract, "Advancing and notices"), so picking Add new asks
 * once for browser notifications: from a click, never on load, and only while the browser has not been answered.
 */
function askToNotify() {
  try {
    if (typeof Notification !== 'undefined' && Notification.permission === 'default') void Notification.requestPermission()
  } catch { /* no Notification API: the toasts still say it */ }
}

/** New idea (contract, "Idea sessions (dashboard)"): a target (an existing repo, or a new one), a base, the idea. */
export function NewSession() {
  const { config } = useConfig()
  const { board } = useData()
  const { openModal, toast, refresh } = useUi()
  const repos = useQuery({ queryKey: ['repos'], queryFn: () => getJson<string[] | Result>('/api/repos'), staleTime: 0 })
  const [target, setTarget] = useState(NEW)
  const [other, setOther] = useState('')
  const [base, setBase] = useState('')
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)

  const listed = Array.isArray(repos.data) ? repos.data : []
  const known = [...new Set([...board.ideas.map((i) => i.target), ...board.jobs.map((j) => j.target)].filter(Boolean) as string[])]
  const options = [...new Set([...listed, ...known])]
  const note = repos.isPending ? 'loading your repos…' : Array.isArray(repos.data) ? '' : (repos.data?.message || 'could not list your repos')
  const t = target === OTHER ? other.trim() : target
  // Base: main, or an unmerged job on the target that has a PR (contract, "Stacked jobs").
  const bases = board.jobs.filter((j) => j.target === t && !['done', 'discarded'].includes(j.stage) && j.pr)
  const chosen = bases.some((j) => j.id === base) ? base : ''   // a base that is not on this target is main

  const pick = (value: string) => {
    setTarget(value)
    if (value === NEW) askToNotify()
  }
  const start = async () => {
    if (target === NEW) askToNotify()
    setBusy(true)
    const r = await api('POST', '/api/sessions', target === NEW ? { new: true, text: text.trim() } : { target: t, text: text.trim(), base: chosen || undefined })
    if (!r.ok) { toast(r.message || 'refused', true); setBusy(false); return }
    await refresh()
    openModal('session', r.message)
  }
  return (
    <>
      <Head>
        <CloseButton />
        <Title>New idea</Title>
        <SubMeta>Claude grills you on it, writes a brief and specs, and you decide what reaches the board. Model <code>{config.model}</code></SubMeta>
      </Head>
      <Body>
        <div className="grid max-w-[640px] grid-cols-[140px_1fr] items-center gap-x-3 gap-y-2">
          <label htmlFor="s-target">Target repo</label>
          <div>
            <select id="s-target" value={target} onChange={(e) => pick(e.target.value)} className={control}>
              <option value={NEW}>+ Add new (Claude grills you and names the repo)</option>
              {options.map((o) => <option key={o} value={o}>{o}</option>)}
              <option value={OTHER}>Other owner/name…</option>
            </select>{' '}
            <span id="s-repos-note" className="text-xs text-content-muted">{note}</span>
            <input id="s-other" autoComplete="off" hidden={target !== OTHER} placeholder={`${config.owner || 'owner'}/todo-app`}
              value={other} onChange={(e) => setOther(e.target.value)} className={control} />
          </div>
          <label htmlFor="s-base" id="s-base-label" hidden={!bases.length}>Base</label>
          <div id="s-base-row" hidden={!bases.length}>
            <select id="s-base" value={chosen} onChange={(e) => setBase(e.target.value)} className={control}>
              <option value="">main</option>
              {bases.map((j) => <option key={j.id} value={j.id}>{j.id}: {j.title} (unmerged, {j.stage})</option>)}
            </select>{' '}
            <span className="text-xs text-content-muted">An unmerged job: the session reads its branch, and the specs build on it.</span>
          </div>
          <label htmlFor="s-text">The idea</label>
          <textarea id="s-text" rows={5} autoFocus value={text} onChange={(e) => setText(e.target.value)}
            placeholder="What do you want built? A sentence is enough: Claude asks the rest." className={control} />
          <span />
          <Actions className="mt-0">
            <Button id="s-start" disabled={busy} onClick={start}>Start</Button>
            {target === NEW
              ? <span id="s-new-note" className="text-sm">A new repo is set up first by a bootstrap job. Its feature specs are written after you merge it.</span>
              : <span className="text-xs text-content-muted">An existing repo, or a new one that a bootstrap job creates and scaffolds.</span>}
          </Actions>
        </div>
      </Body>
    </>
  )
}

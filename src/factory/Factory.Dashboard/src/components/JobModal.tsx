import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { POLL_MS, useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { control } from '@/components/ui/control'
import { api, enc, getJson, getText } from '@/lib/api'
import { blockedFromDeploy, BUILDING, preview, usageLimited } from '@/lib/board'
import { cn } from '@/lib/cn'
import { FLAG_MEANS } from '@/lib/flags'
import { md } from '@/lib/markdown'
import type { FactoryEvent, Job } from '@/lib/types'
import { Actions, Body, CloseButton, FlagBox, Head, Md, SubMeta, Tabs, Title } from './Modal'
import { OutputPane } from './OutputPane'

const TABS = ['spec', 'artifacts', 'output', 'timeline']

/** The job's artifact paths (relative to jobs/<id>/), polled while it is open. */
function useFiles(id: string) {
  return useQuery({
    queryKey: ['files', id],
    queryFn: async () => (await getJson<string[]>(`/api/jobs/${enc(id)}/files`)) ?? [],
    refetchInterval: POLL_MS,
  }).data ?? []
}

export function JobModal({ id, focusRework }: { id: string; focusRework: boolean }) {
  const { board } = useData()
  const { setDirty } = useUi()
  const j = board.jobs.find((x) => x.id === id)
  const [tab, setTab] = useState(() => (j && BUILDING.includes(j.stage) ? 'output' : 'spec'))
  const [spec, setSpec] = useState<string | null>(null)
  const files = useFiles(id)
  useEffect(() => { getText(`/state/jobs/${id}/spec.md`).then((t) => setSpec((s) => (s === null ? t : s))) }, [id])
  const edit = (t: string) => { setDirty(true); setSpec(t) }

  if (!j) return <><Head><CloseButton /><p>{id} is not on the board.</p></Head><Tabs tabs={TABS} tab={tab} onTab={setTab} /><Body>{null}</Body></>
  return (
    <>
      <JobHead j={j} focusRework={focusRework} />
      <Tabs tabs={TABS} tab={tab} onTab={setTab} />
      <Body>
        {tab === 'spec' && <SpecTab j={j} spec={spec} onEdit={edit} />}
        {tab === 'artifacts' && <ArtifactsTab j={j} files={files} />}
        {tab === 'output' && <OutputPane kind="job" id={id} files={files} />}
        {tab === 'timeline' && <Timeline j={j} />}
      </Body>
    </>
  )
}

function JobHead({ j, focusRework }: { j: Job; focusRework: boolean }) {
  const { moving, move, closeModal, toast, isDirty, refresh } = useUi()
  const [now] = useState(() => Date.now())
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const noteRef = useRef<HTMLTextAreaElement>(null)
  useEffect(() => { if (focusRework) setTimeout(() => noteRef.current?.focus(), 50) }, [focusRework])

  const flag = j.flag
  const live = j.deploy?.status ? preview(j, now) : null
  const previewBtn = live?.live && live.url && <a className="button" id="preview-btn" href={live.url} target="_blank" rel="noopener">{btn('Open preview')}</a>
  const prBtn = j.pr?.url && <a className="button" id="pr-btn" href={j.pr.url} target="_blank" rel="noopener">{btn(`Open PR #${j.pr.number}`)}</a>
  const gate = j.gate ? Object.entries(j.gate).filter(([k]) => k !== 'at' && k !== 'sha').map(([k, v]) => `${k}=${v}`).join(' ') + (j.gate.sha ? ` · ${j.gate.sha.slice(0, 7)}` : '') : ''
  const m = j.merged

  // A write that takes a while: block a second click, and on success get out of the way so the card is seen moving.
  const run = async (what: string, act: () => Promise<boolean>) => {
    setBusy(what)
    if (await act()) closeModal(true); else setBusy(null)
  }
  const post = (what: 'resume' | 'redeploy', done: string) => run(what, async () => {
    const res = await api('POST', `/api/jobs/${enc(j.id)}/${what}`, {})
    toast(res.ok ? `${j.id}: ${done}` : res.message, !res.ok)
    await refresh()
    return res.ok
  })
  const approve = () => {
    if (isDirty()) { toast('Save the spec first', true); return }
    // factory approve takes seconds: close at once, and the card spins until it lands.
    closeModal(true)
    move(j.id, 'approved')
  }
  const rework = () => {
    if (!note.trim()) { toast('A rework needs a note', true); return }
    run('rework', () => move(j.id, 'approved', note.trim()))
  }
  const copyPaths = async () => {
    try { await navigator.clipboard.writeText([...(m?.added ?? []), ...(m?.modified ?? [])].join('\n')); toast('Paths copied') } catch { toast('Copy failed', true) }
  }

  const discard = <Button id="discard-btn" variant="danger" disabled={moving.has(j.id)} onClick={() => move(j.id, 'discarded')}>Discard</Button>
  const resume = usageLimited(j) && <Button id="resume-btn" disabled={busy === 'resume'} onClick={() => post('resume', 'resuming')}>Resume</Button>
  let actions = null
  if (j.stage === 'backlog') actions = moving.has(j.id)
    ? <><Button id="approve-btn" loading>Approving…</Button>{discard}</>
    : <><Button id="approve-btn" onClick={approve}>Approve</Button>{discard}</>
  if (j.stage === 'ready') actions = <>{resume}{previewBtn}{prBtn}<Button id="merge-btn" disabled={busy === 'merge'} onClick={() => run('merge', () => move(j.id, 'done'))}>{busy === 'merge' ? 'Merging…' : 'Merge (Done)'}</Button>{discard}</>
  if (j.stage === 'blocked') actions = <>{resume}{previewBtn}{prBtn}
    {!usageLimited(j) && blockedFromDeploy(j) && <Button id="redeploy-btn" disabled={busy === 'redeploy'} onClick={() => post('redeploy', 'retrying deploy')}>Retry deploy</Button>}{discard}</>

  return (
    <Head>
      <CloseButton />
      <Title>{j.title}</Title>
      <SubMeta>
        {j.id} · {j.stage} · round {j.round}{j.idea && ` · from ${j.idea}`}{j.costUsd != null && ` · $${Number(j.costUsd).toFixed(2)}`}{!!j.durationMs && ` · ${Math.round(j.durationMs / 1000)}s`}
        {j.target && <><br />target <code>{j.target}</code></>}
        {j.pr?.url && <> · <a data-pr href={j.pr.url} target="_blank" rel="noopener" className="text-brand">PR #{j.pr.number}</a></>}
        {gate && <> · gate <span data-gate>{gate}</span></>}
      </SubMeta>
      {flag && (
        <FlagBox data-flagbox>
          <b>{flag.reason}</b>{FLAG_MEANS[flag.reason] && `: ${FLAG_MEANS[flag.reason]}`}
          {flag.why ? <Md html={md(flag.why)} /> : <div>{flag.detail}</div>}
        </FlagBox>
      )}
      {m && (
        <div className="merged mt-2 rounded-control bg-intent-success-subtle px-2.5 py-2 text-[12.5px]" data-merged>
          Merged into <code>{m.repo}</code> @ <code>{m.sha.slice(0, 7)}</code>
          {m.pr && <> by <a href={m.pr} target="_blank" rel="noopener" className="underline">pull request</a></>}{' '}
          <Button id="copy-paths" variant="secondary" size="sm" onClick={copyPaths}>Copy paths</Button>
          <ul className="mt-1 mb-0 max-h-[18vh] overflow-auto pl-4 font-mono text-xs">
            {(m.added ?? []).map((p) => <li key={`A${p}`}><span className="text-content-muted">A</span> {p}</li>)}
            {(m.modified ?? []).map((p) => <li key={`M${p}`}><span className="text-content-muted">M</span> {p}</li>)}
          </ul>
        </div>
      )}
      {actions && <Actions>{actions}</Actions>}
      {(j.stage === 'ready' || j.stage === 'blocked') && (
        <div className="rework mt-2">
          <textarea id="rework-note" ref={noteRef} rows={2} value={note} onChange={(e) => setNote(e.target.value)}
            placeholder="Rework: what should change? The note goes to the builder."
            className={cn(control, 'font-mono text-[12.5px]')} />
          <Actions><Button id="rework-btn" variant="secondary" disabled={busy === 'rework'} onClick={rework}>{busy === 'rework' ? 'Reworking…' : 'Rework'}</Button></Actions>
        </div>
      )}
    </Head>
  )
}

/** A link styled as the template's secondary button. */
function btn(text: string) {
  return <span className="inline-flex items-center rounded-control border border-border-subtle bg-surface-raised px-3.5 py-2 text-sm font-medium text-content hover:border-brand">{text}</span>
}

function SpecTab({ j, spec, onEdit }: { j: Job; spec: string | null; onEdit: (t: string) => void }) {
  const { toast, setDirty } = useUi()
  if (j.stage !== 'backlog') return <Md html={md(spec)} />
  const save = async () => {
    const res = await api('PUT', `/api/jobs/${j.id}/spec`, spec ?? '', 'text/markdown')
    if (res.ok) { setDirty(false); toast(res.message || 'spec saved') } else toast(res.message, true)
  }
  return (
    <div className="grid h-full min-h-0 grid-cols-1 gap-3 md:grid-cols-2">
      <div className="flex min-h-0 flex-col gap-2">
        <textarea id="spec-editor" spellCheck={false} value={spec ?? ''} onChange={(e) => onEdit(e.target.value)}
          className={cn(control, 'min-h-[200px] flex-1 resize-none font-mono text-[12.5px]')} />
        <Actions className="mt-0">
          <Button id="spec-save" onClick={save}>Save spec</Button>
          <span className="text-xs text-content-muted">Edit on the left; the preview is live.</span>
        </Actions>
      </div>
      <Md id="spec-preview" html={md(spec)} className="overflow-auto rounded-control border border-border-subtle px-3.5 py-1" />
    </div>
  )
}

function ArtifactsTab({ j, files }: { j: Job; files: string[] }) {
  const [viewer, setViewer] = useState<{ file: string; text: string } | null>(null)
  const all = ['spec.md', ...files.filter((f) => f !== 'spec.md')]
  const pick = async (file: string) => setViewer({ file, text: (await getText(`/state/jobs/${j.id}/${file}`)) || '(empty)' })
  return (
    <div className="grid h-full min-h-0 grid-cols-1 gap-3 md:grid-cols-[220px_1fr]">
      <div className="flex flex-col gap-0.5 overflow-auto">
        {all.map((f) => (
          <a key={f} href="#" data-file={f} onClick={(e) => { e.preventDefault(); pick(f) }}
            className={cn('rounded-control px-1.5 py-0.5 font-mono text-xs text-brand no-underline', f === viewer?.file && 'on bg-brand-subtle')}>{f}</a>
        ))}
      </div>
      <div id="viewer" className="min-h-0 overflow-auto">
        {!viewer ? <p className="text-content-muted">Pick a file.</p>
          : viewer.file.endsWith('.md') ? <Md html={md(viewer.text)} />
          : <pre className="m-0 overflow-auto rounded-control border border-border-subtle bg-surface-sunken px-2.5 py-2 font-mono text-[12.5px] break-words whitespace-pre-wrap">{viewer.text}</pre>}
      </div>
    </div>
  )
}

function timelineText(e: FactoryEvent): string {
  if (e.kind === 'stage') return `${e.from ?? '∅'} → ${e.to}${e.by ? ` (${e.by})` : ''}${e.reason ? ` [${e.reason}]` : ''}`
  if (e.kind === 'cost') return `cost $${Number(e.costUsd).toFixed(2)}, ${Math.round(Number(e.durationMs) / 1000)}s`
  return `${e.kind}${e.agent ? ` ${e.agent}` : ''}${e.round ? ` r${e.round}` : ''}${e.detail ? ` — ${e.detail}` : ''}`
}

function Timeline({ j }: { j: Job }) {
  const { events } = useData()
  const items = events.filter((e) => e.job === j.id || (j.idea && e.idea === j.idea && !e.job))
  return (
    <ul data-timeline className="m-0 list-none p-0 text-[12.5px]">
      {items.map((e, i) => (
        <li key={i} className="grid grid-cols-[80px_1fr] gap-2 border-b border-dotted border-border-subtle py-0.5">
          <time className="font-mono text-content-muted">{new Date(e.ts ?? '').toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</time>
          <span>{timelineText(e)}</span>
        </li>
      ))}
    </ul>
  )
}

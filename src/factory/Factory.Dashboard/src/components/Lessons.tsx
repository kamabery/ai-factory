import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useConfig } from '@/app/data'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { api, enc, getJson, getText } from '@/lib/api'
import { leaf } from '@/lib/board'
import { cn } from '@/lib/cn'
import { proposeText } from '@/lib/evals'
import { md } from '@/lib/markdown'
import { BodyPane, Empty, ListPane, VIEW_POLL_MS } from './Evals'

type Lesson = { job: string; title?: string; target?: string; status: string; session?: string; flag?: string; templateGaps?: number; at?: string }

/** What jobs that went bad taught (contract, "Lessons"), and Propose template fix on the selected ones. */
export function Lessons() {
  const { config } = useConfig()
  const { toast } = useUi()
  const qc = useQueryClient()
  const [open, setOpen] = useState<string | null>(null)
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set())
  const list = useQuery({ queryKey: ['lessons'], queryFn: async () => (await getJson<Lesson[]>('/api/lessons')) ?? [], refetchInterval: VIEW_POLL_MS }).data ?? []
  const body = useQuery({ queryKey: ['lesson-md', open], queryFn: () => getText(`/state/lessons/${enc(open!)}.md`), enabled: !!open }).data
  const repo = config.templatesRepo

  const mark = async (id: string, status: string, session?: string) => {
    const res = await api('POST', `/api/lessons/${enc(id)}/mark`, session ? { status, session } : { status })
    if (!res.ok) toast(res.message, true)
    await qc.invalidateQueries({ queryKey: ['lessons'] })
  }
  // The selected lessons become one idea session on the templates repo, so the fix goes through the factory under the
  // human's approval like any other change.
  const propose = async () => {
    if (!repo) { toast('Set FACTORY_TEMPLATES_REPO in .env to the template-projects repo', true); return }
    const ids = [...picked]
    const bodies = await Promise.all(ids.map((id) => getText(`/state/lessons/${enc(id)}.md`)))
    if (!config.enabled) { toast(`Idea sessions are off in this run mode: run factory idea ${repo} and paste the lessons`, true); return }
    const res = await api('POST', '/api/sessions', { target: repo, text: proposeText(ids, bodies) })
    if (!res.ok) { toast(res.message, true); return }
    for (const id of ids) await mark(id, 'proposed', res.message)
    setPicked(new Set())
    toast(`Proposed as ${res.message} on ${repo}`)
  }
  const toggle = (id: string, on: boolean) => setPicked((p) => { const n = new Set(p); if (on) n.add(id); else n.delete(id); return n })

  return (
    <main id="lessons" className="grid grid-cols-1 items-start gap-3 p-4 md:grid-cols-[minmax(320px,2fr)_3fr]">
      <ListPane>
        <div className="mb-1 flex items-center gap-2">
          <Button data-propose disabled={!picked.size} onClick={propose}>Propose template fix</Button>
          <small>{repo ? `as an idea on ${repo}` : 'set FACTORY_TEMPLATES_REPO in .env'}</small>
        </div>
        {list.length ? list.map((x) => (
          <div key={x.job} className={cn('card eval-row lesson-row', open === x.job && 'selected')} data-lesson={x.job} onClick={() => setOpen(x.job)}>
            <div className="top">
              <span>
                {x.status === 'open' && (
                  <input type="checkbox" data-select className="mr-1.5" aria-label={`select ${x.job}`} checked={picked.has(x.job)}
                    onClick={(e) => e.stopPropagation()} onChange={(e) => toggle(x.job, e.target.checked)} />
                )}
                {x.job}
              </span>
              <span>{String(x.at ?? '').slice(0, 10)}</span>
            </div>
            <div className="title">{x.title}</div>
            {x.target && <div className="repo" title={x.target}>→ {leaf(x.target)}</div>}
            <div className="meta">
              <span className="chip" data-status>{x.status}{x.session ? ` ${x.session}` : ''}</span>
              {x.flag && <span className="flag">{x.flag}</span>}
              <span data-gaps>{x.templateGaps ? `${x.templateGaps} template gap${x.templateGaps > 1 ? 's' : ''}` : 'no template gaps'}</span>
              {x.status === 'proposed' && (
                <button type="button" className="chip action cursor-pointer bg-surface-raised text-content" data-applied
                  onClick={(e) => { e.stopPropagation(); mark(x.job, 'applied') }}>Mark applied</button>
              )}
            </div>
          </div>
        )) : <Empty>No lessons yet. A job that ends flagged, or whose devops report names a template gap, leaves one here.</Empty>}
      </ListPane>
      <BodyPane data-lesson-body>
        {open ? <div dangerouslySetInnerHTML={{ __html: md(body ?? '') }} /> : <p className="eval-empty py-3 text-content-muted">Pick a lesson.</p>}
      </BodyPane>
    </main>
  )
}

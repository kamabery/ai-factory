import { useLayoutEffect, useRef } from 'react'
import { useQuery } from '@tanstack/react-query'
import { POLL_MS, useData } from '@/app/data'
import { enc, getText } from '@/lib/api'
import { cn } from '@/lib/cn'
import { streamLines } from '@/lib/stream'
import type { FactoryEvent } from '@/lib/types'

type Source = { title: string; who: string; url: string }

/** The orchestrator's run logs (a job's run-<n>.jsonl) and each agent's transcript, in order. */
function outputSources(kind: 'job' | 'idea', id: string, files: string[], events: FactoryEvent[]): Source[] {
  const sources: Source[] = []
  if (kind === 'job') {
    for (const f of files.filter((f) => /^run-\d+\.jsonl$/.test(f)).sort((a, b) => parseInt(a.slice(4)) - parseInt(b.slice(4))))
      sources.push({ title: f.replace('.jsonl', ''), who: 'orchestrator', url: `/state/jobs/${id}/${f}` })
  }
  for (const e of events.filter((e) => e.kind === 'agent-start' && e.agentId && (kind === 'job' ? e.job === id : e.idea === id && !e.job)))
    sources.push({ title: `${e.agent}${e.round ? ` · round ${e.round}` : ''}`, who: String(e.agent), url: `/api/agents/${enc(String(e.agentId))}/transcript` })
  return sources
}

/** The Output tab: a terminal that follows its end while you are at it. */
export function OutputPane({ kind, id, files }: { kind: 'job' | 'idea'; id: string; files: string[] }) {
  const { events } = useData()
  const sources = outputSources(kind, id, files, events)
  const texts = useQuery({
    queryKey: ['output', kind, id, sources.map((s) => s.url)],
    queryFn: () => Promise.all(sources.map((s) => getText(s.url))),
    refetchInterval: POLL_MS,
    placeholderData: (previous) => previous,   // a new transcript appearing keeps the rest on screen
  }).data
  const pane = useRef<HTMLDivElement>(null)
  const atBottom = useRef(true)
  useLayoutEffect(() => { if (atBottom.current && pane.current) pane.current.scrollTop = pane.current.scrollHeight }, [texts])

  const agents: Record<string, string> = {}
  const blocks = texts ? sources.map((s, k) => ({ s, lines: (texts[k] ?? '').split('\n').flatMap((l) => streamLines(l, s.who, agents)) })) : []
  return (
    <div className="term" data-output ref={pane}
      onScroll={(e) => { const p = e.currentTarget; atBottom.current = p.scrollHeight - p.scrollTop - p.clientHeight < 40 }}>
      {blocks.length === 0 && <div className="ln dim">No output yet.</div>}
      {blocks.map(({ s, lines }, k) => (
        <div key={k}>
          <div className="sep">── {s.title} ──</div>
          {lines.map((l, i) => (
            <div key={i} className={cn('ln', l.cls)} data-agent={l.who}><span className="tag">[{l.who}]</span>{l.text}</div>
          ))}
        </div>
      ))}
    </div>
  )
}

// The Output tab: the orchestrator's stream-json and each agent's transcript as terminal lines.

export type Line = { who: string; cls: string; text: string }

type Block = { type?: string; text?: string; thinking?: string; id?: string; name?: string; input?: Record<string, unknown>; content?: unknown }

export function summarize(input: unknown): string {
  if (!input || typeof input !== 'object') return ''
  const i = input as Record<string, unknown>
  const v = i.description ?? i.command ?? i.file_path ?? i.pattern ?? i.path ?? i.subagent_type ?? JSON.stringify(input)
  const s = String(v).replace(/\s+/g, ' ')
  return s.length > 160 ? s.slice(0, 160) + '…' : s
}

export function resultText(c: unknown): string {
  const t = typeof c === 'string' ? c : Array.isArray(c) ? c.map((x: { text?: string }) => x.text ?? '').join(' ') : ''
  const s = t.replace(/\s+/g, ' ').trim()
  return s.length > 200 ? s.slice(0, 200) + '…' : s
}

type StreamLine = {
  type?: string; subtype?: string; model?: string; parent_tool_use_id?: string
  total_cost_usd?: number; duration_ms?: number; message?: { content?: unknown }
}

/** One stream line as terminal lines. `who` is the default tag; `agents` maps Agent tool ids to names, across lines. */
export function streamLines(raw: string, who: string, agents: Record<string, string>): Line[] {
  let o: StreamLine
  try { o = JSON.parse(raw) } catch { return raw.trim() ? [{ who, cls: 'dim', text: raw }] : [] }
  const tag = o.parent_tool_use_id ? (agents[o.parent_tool_use_id] ?? 'agent') : who
  const out: Line[] = []
  if (o.type === 'system' && o.subtype === 'init') out.push({ who: tag, cls: 'dim', text: `● session started${o.model ? ` (${o.model})` : ''}` })
  if (o.type === 'result') out.push({ who: tag, cls: 'done', text: `■ ${o.subtype ?? 'done'} · $${Number(o.total_cost_usd ?? 0).toFixed(2)} · ${Math.round((o.duration_ms ?? 0) / 1000)}s` })
  const content = o.message?.content
  if (o.type === 'user' && typeof content === 'string') out.push({ who: tag, cls: 'dim', text: `› ${resultText(content)}` })
  if (Array.isArray(content)) for (const b of content as Block[]) {
    if (b.type === 'thinking') out.push({ who: tag, cls: 'thinking', text: b.thinking ? `💭 ${b.thinking}` : '💭 thinking…' })
    else if (b.type === 'text' && o.type === 'assistant') out.push({ who: tag, cls: '', text: String(b.text) })
    else if (b.type === 'text' && o.type === 'user') out.push({ who: tag, cls: 'dim', text: `› ${resultText(b.text)}` })
    else if (b.type === 'tool_use') {
      if (b.name === 'Agent' || b.name === 'Task') agents[String(b.id)] = String(b.input?.subagent_type ?? 'agent').replace(/^factory:/, '')
      out.push({ who: tag, cls: 'tool', text: `▸ ${b.name}(${summarize(b.input)})` })
    } else if (b.type === 'tool_result') out.push({ who: tag, cls: 'res', text: `  ↳ ${resultText(b.content)}` })
  }
  return out
}

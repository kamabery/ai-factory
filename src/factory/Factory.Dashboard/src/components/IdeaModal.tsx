import { useEffect, useState } from 'react'
import { useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { Button } from '@/components/ui/button'
import { getText } from '@/lib/api'
import { md } from '@/lib/markdown'
import { Actions, Body, CloseButton, FlagBox, Head, Md, SubMeta, Tabs, Title } from './Modal'
import { OutputPane } from './OutputPane'

/** An idea card opens its brief, and the spec-writer's output (contract, "Dashboard API"). */
export function IdeaModal({ id }: { id: string }) {
  const { board } = useData()
  const { abandon } = useUi()
  const [tab, setTab] = useState('brief')
  const [brief, setBrief] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => { getText(`/state/ideas/${id}/brief.md`).then((t) => setBrief(t || '(no brief)')) }, [id])
  const idea = board.ideas.find((x) => x.id === id)
  const canAbandon = idea && ['waiting', 'drafting', 'failed'].includes(idea.status)
  return (
    <>
      <Head>
        <CloseButton />
        {idea ? (
          <>
            <Title>{idea.text}</Title>
            <SubMeta>
              {idea.id} · {idea.status}{idea.target && <> · target <code>{idea.target}</code></>}{idea.jobs?.length ? ` · ${idea.jobs.join(', ')}` : ''}
            </SubMeta>
            {idea.reason && <FlagBox>{idea.reason}</FlagBox>}
            {canAbandon && (
              <Actions>
                <Button id="idea-abandon-btn" variant="danger" disabled={busy} onClick={() => { setBusy(true); abandon(idea.id, true) }}>Abandon</Button>
              </Actions>
            )}
          </>
        ) : <p>{id} is not on the board.</p>}
      </Head>
      <Tabs tabs={['brief', 'output']} tab={tab} onTab={setTab} />
      <Body>
        {tab === 'brief' && <Md html={md(brief ?? '')} data-brief />}
        {tab === 'output' && <OutputPane kind="idea" id={id} files={[]} />}
      </Body>
    </>
  )
}

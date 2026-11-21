import { useEffect, useRef, useState } from 'react'
import { useData } from '@/app/data'
import { useUi } from '@/app/ui'
import { noticeText } from '@/lib/session'

const KINDS = ['scaffold-ready', 'specs-ready'] as const

/**
 * The two moments a new-repo session needs the human (contract, "Advancing and notices"): the server records each
 * once, and the page shows one recorded after it loaded as a toast that stays, plus a browser notification when the
 * human granted them. One recorded before the page loaded is not repeated: the session card says it instead.
 */
export function SessionNotices() {
  const { sessions } = useData()
  const { toast } = useUi()
  const [loadedAt] = useState(() => Date.now())
  const shown = useRef(new Set<string>())
  useEffect(() => {
    for (const x of sessions) {
      for (const kind of KINDS) {
        const at = x.notices?.[kind]
        const key = `${x.id}:${kind}`
        if (!at || shown.current.has(key) || !(Date.parse(at) > loadedAt)) continue
        shown.current.add(key)
        const text = noticeText(kind, x)
        toast(text, false, { sticky: true })
        try {
          if (typeof Notification !== 'undefined' && Notification.permission === 'granted') new Notification('Factory', { body: text })
        } catch { /* the toast still says it */ }
      }
    }
  }, [sessions, loadedAt, toast])
  return null
}

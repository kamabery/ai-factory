import { fireEvent, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { config, fakeServer, find, renderApp, writes } from '@/test/server'

afterEach(() => vi.unstubAllGlobals())

const status = (message: string) => ({ 'GET /api/platform': { ok: true, message } })

describe('the platform chip (spec 24)', () => {
  it('says asleep with Start, and Start posts and shows starting with the moving marker', async () => {
    const calls = fakeServer({ ...status('asleep'), 'POST /api/platform/start': { ok: true, message: 'starting the platform' } })
    const r = renderApp()
    const chip = await find('#platform')
    await waitFor(() => expect(chip).toHaveTextContent('platform: asleep'))
    expect(chip).toHaveAttribute('data-platform', 'asleep')
    expect(r.$('#platform [data-platform-sleep]')).toBeNull()
    fireEvent.click(r.$('#platform [data-platform-start]')!)
    await waitFor(() => expect(writes(calls)).toContainEqual({ method: 'POST', url: '/api/platform/start', body: {} }))
    await waitFor(() => expect(chip).toHaveTextContent('platform: starting'))
    expect(chip.querySelector('[data-spinner]')).not.toBeNull()
    expect(r.$('#platform [data-platform-start]')).toBeNull()
  })

  it('says awake with Sleep, which posts sleep', async () => {
    const calls = fakeServer({ ...status('awake'), 'POST /api/platform/sleep': { ok: true, message: 'platform: stopping' } })
    const r = renderApp()
    await waitFor(() => expect(r.$('#platform')).toHaveTextContent('platform: awake'))
    expect(r.$('#platform [data-platform-start]')).toBeNull()
    fireEvent.click(r.$('#platform [data-platform-sleep]')!)
    await waitFor(() => expect(writes(calls)).toContainEqual({ method: 'POST', url: '/api/platform/sleep', body: {} }))
    await waitFor(() => expect(r.$('#platform')).toHaveTextContent('platform: stopping'))
  })

  it.each(['starting', 'stopping'])('while %s it shows the moving marker and no button', async (s) => {
    fakeServer(status(s))
    const r = renderApp()
    await waitFor(() => expect(r.$('#platform')).toHaveTextContent(`platform: ${s}`))
    expect(r.$('#platform [data-spinner]')).not.toBeNull()
    expect(r.$('#platform button')).toBeNull()
  })

  it('a refused sleep stays on screen and names what holds the platform', async () => {
    fakeServer({ ...status('awake'), 'POST /api/platform/sleep': { ok: false, message: "platform busy: J-039's preview is up" } })
    const r = renderApp()
    fireEvent.click(await find('#platform [data-platform-sleep]'))
    await waitFor(() => expect(r.$('#toast')).toHaveTextContent("platform busy: J-039's preview is up"))
    expect(r.$('#toast')).toHaveClass('error')
    expect(r.$('#platform')).toHaveTextContent('platform: awake')
  })

  it('is not shown, and not asked for, with deploys off', async () => {
    const calls = fakeServer({ 'GET /api/ideate/config': { ...config, deploys: false }, ...status('awake') })
    const r = renderApp()
    await find('#mode')
    await new Promise((res) => setTimeout(res, 50))
    expect(r.$('#platform')).toBeNull()
    expect(calls.some((c) => c.url === '/api/platform')).toBe(false)
  })

  it('the canary banner names the wake phase', async () => {
    fakeServer({ 'GET /state/canary.json': { status: 'running', startedAt: new Date().toISOString(), phase: 'wake' } })
    const r = renderApp()
    await waitFor(() => expect(r.$('#canary')).toHaveTextContent('waking the platform · 0 of ~15 min'))
  })
})

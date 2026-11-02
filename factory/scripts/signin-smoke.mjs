// Deploy step 7a, the sign-in smoke (see "Deploy" in docs/FACTORY-CONTRACT.md).
//
//   node signin-smoke.mjs --url <front end> --user <qa user> --shots <dir> [--seconds 120]
//   (the password on stdin, never as an argument)
//
// Opens the front end headless, clicks Sign in, completes the identity provider's form as the QA
// user (Entra External ID: email, then password; Keycloak: both on one page), and passes when the
// browser is back on the front end's origin, the header shows Sign out, and the app's own
// GET /api/me (sent with the access token) answered 200. Prints one line per step reached
// (`step: ...`), then `url:` (no query), `error:` and `screenshot:` on a failure, and exits 1.
// Never prints the password. Playwright comes from factory/tests/dashboard's node_modules.
import { createRequire } from 'node:module'
import { mkdirSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const { chromium } = createRequire(join(here, '../tests/dashboard/package.json'))('playwright')

const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`)
  return i > 0 && process.argv[i + 1] !== undefined ? process.argv[i + 1] : fallback
}
const url = arg('url')
const user = arg('user')
const shots = arg('shots', '.')
const seconds = Number(arg('seconds', '120'))
if (!url || !user) { console.log('error: usage: signin-smoke.mjs --url <front end> --user <qa user> --shots <dir> (password on stdin)'); process.exit(2) }

let password = ''
for await (const chunk of process.stdin) password += chunk
password = password.trim()
if (!password) { console.log('error: no password on stdin'); process.exit(2) }

const step = (s) => console.log(`step: ${s}`)
const noQuery = (u) => { try { const x = new URL(u); return `${x.origin}${x.pathname}` } catch { return u } }
const origin = new URL(url).origin
const deadline = Date.now() + seconds * 1000
const left = () => Math.max(1, deadline - Date.now())
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const browser = await chromium.launch()
const page = await browser.newPage()
const me = []
page.on('response', (r) => { if (new URL(r.url()).pathname.replace(/\/$/, '') === '/api/me') me.push(r.status()) })

async function fail(why) {
  let text = ''
  try { text = (await page.locator('body').innerText({ timeout: 2000 })).replace(/\s+/g, ' ').slice(0, 400) } catch {}
  console.log(`url: ${noQuery(page.url())}`)
  console.log(`error: ${why}`)
  if (text) console.log(`page: ${text.replaceAll(password, '***')}`)
  try {
    mkdirSync(shots, { recursive: true })
    const shot = join(shots, `signin-${Date.now()}.png`)
    await page.screenshot({ path: shot, fullPage: true })
    console.log(`screenshot: ${shot}`)
  } catch {}
  await browser.close()
  process.exit(1)
}

// An identity provider's error: an AADSTS code on the page, or error= on the url.
async function providerError() {
  const u = new URL(page.url())
  if (u.searchParams.get('error')) return `${u.searchParams.get('error')}: ${u.searchParams.get('error_description') ?? ''}`.trim()
  const body = await page.locator('body').innerText({ timeout: 1000 }).catch(() => '')
  const m = body.match(/AADSTS\d+[^\n]*/)
  return m ? m[0] : null
}

const visible = async (loc) => (await loc.count()) > 0 && (await loc.first().isVisible().catch(() => false))
const submit = async () => {
  const b = page.locator('button[type=submit], input[type=submit]').filter({ visible: true })
  if (await b.count()) await b.first().click()
  else await page.keyboard.press('Enter')
}

try {
  // A fresh container can take a while: reload until the app offers Sign in.
  const signIn = page.getByRole('button', { name: /^sign in$/i })
  while (true) {
    await page.goto(url, { timeout: left() }).catch(() => {})
    if (await signIn.waitFor({ timeout: Math.min(15000, left()) }).then(() => true, () => false)) break
    if (Date.now() >= deadline) await fail('the front end never showed Sign in')
  }
  step('opened the front end; Sign in is showing')
  await signIn.click()
  step('clicked Sign in')

  // The provider's form, page by page, until the browser is back on the front end.
  let userDone = false, passwordDone = false, kmsi = false
  while (true) {
    if (Date.now() >= deadline) await fail(`timed out after ${seconds}s on the identity provider`)
    const err = await providerError()
    if (err) await fail(err)
    if (new URL(page.url()).origin === origin && (userDone || passwordDone)) break
    const userBox = page.locator('input[type=email], input[name=loginfmt], input[name=username], input[autocomplete=username]').filter({ visible: true })
    const pwBox = page.locator('input[type=password]').filter({ visible: true })
    if (!userDone && await visible(userBox)) {
      await userBox.first().fill(user)
      userDone = true
      step(`entered the user on ${noQuery(page.url())}`)
      if (!(await visible(pwBox))) { await submit(); await sleep(1000); continue }
    }
    if (!passwordDone && await visible(pwBox)) {
      await pwBox.first().fill(password)
      passwordDone = true
      step('entered the password')
      await submit()
      await sleep(1000)
      continue
    }
    // Entra's "Stay signed in?" interstitial.
    const no = page.getByRole('button', { name: /^no$/i })
    if (!kmsi && passwordDone && await visible(no)) { await no.first().click(); kmsi = true; step('answered Stay signed in: No'); continue }
    await sleep(500)
  }
  step(`back on the front end at ${noQuery(page.url())}`)

  // Signed in: the header shows Sign out, and the app's own /api/me answered 200.
  const signOut = page.getByRole('button', { name: /^sign out$/i })
  const failed = page.getByText(/sign-in did not complete/i)
  while (true) {
    if (await visible(failed)) await fail(`the app says sign-in did not complete`)
    if (await visible(signOut)) break
    if (Date.now() >= deadline) await fail('back on the front end, but Sign out never showed')
    await sleep(500)
  }
  step('signed in: Sign out is showing')
  while (!me.includes(200)) {
    if (Date.now() >= deadline) await fail(`the app's GET /api/me answered ${me.length ? me.join(', ') : 'nothing'}, not 200`)
    await sleep(500)
  }
  step('/api/me answered 200')
  await browser.close()
  process.exit(0)
} catch (e) {
  await fail(String(e?.message ?? e).split('\n')[0])
}

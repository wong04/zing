import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { createServer, type IncomingMessage, type Server } from 'node:http'
import { createServer as createSocketServer, type AddressInfo } from 'node:net'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, pages, scratchDir, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

// No test here reaches the internet: the host asks a DoH server this file runs on 127.0.0.1, and the probes find what the
// test says (ZING_E2E_PROBES, see Zing.Host/TestHooks.NetServices).

interface World {
	readonly system: readonly string[]
	readonly tcp: boolean
	readonly tlsValid: boolean
}

interface LocalDoh {
	readonly url: string
	readonly asked: string[]
	close(): Promise<void>
}

async function serveDoh(answer: readonly string[]): Promise<LocalDoh> {
	const asked: string[] = []
	const server: Server = createServer((request: IncomingMessage, response) => {
		const name = new URL(request.url ?? '/', 'http://127.0.0.1').searchParams.get('name') ?? ''
		asked.push(name)
		response.setHeader('Content-Type', 'application/dns-json')
		response.end(JSON.stringify({ Status: 0, Answer: answer.map((data) => ({ name, type: 1, data })) }))
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	const { port } = server.address() as AddressInfo
	return { url: `http://127.0.0.1:${port}/dns-query`, asked, close: () => new Promise((resolve) => server.close(() => resolve())) }
}

/** An address nothing listens on, standing in for a DoH service the network blocks. */
async function deadAddress(): Promise<string> {
	const probe = createSocketServer()
	await new Promise<void>((resolve) => probe.listen(0, '127.0.0.1', resolve))
	const { port } = probe.address() as AddressInfo
	await new Promise<void>((resolve) => probe.close(() => resolve()))
	return `http://127.0.0.1:${port}/dns-query`
}

async function launchWith(dohUrl: string, world: World): Promise<Launched> {
	return launchZing({ env: { ZING_E2E_DOH_URL: dohUrl, ZING_E2E_PROBES: JSON.stringify(world) } })
}

async function openDiagnose(zing: Launched): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').filter({ hasText: 'Diagnose' }).click()
	const panel = surface.getByRole('dialog', { name: 'Diagnose' })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

const steps = (panel: Locator): Locator => panel.getByRole('textbox', { name: 'Steps' })

async function diagnose(panel: Locator, site: string): Promise<void> {
	await panel.getByRole('textbox', { name: 'Site' }).fill(site)
	await panel.getByRole('button', { name: 'Diagnose', exact: true }).click()
}

test('Diagnose reports a local server as not blocked', async () => {
	const doh = await serveDoh(['93.184.216.34'])
	const zing = await launchWith(doh.url, { system: ['93.184.216.34'], tcp: true, tlsValid: true })
	const { panel } = await openDiagnose(zing)
	await expect(panel.getByTestId('doh-notice')).toContainText("Cloudflare receives the site's name")
	await diagnose(panel, 'local.test')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('Not blocked')
	await expect(panel.getByRole('group', { name: 'Network DNS' })).toContainText('93.184.216.34')
	await expect(panel.getByRole('group', { name: 'Public DNS' })).toContainText('93.184.216.34')
	await expect(steps(panel)).toHaveValue(/Your network's address serves a valid certificate for local\.test/)
	// The host really asked the DoH server over HTTP, for the name that was typed.
	expect(doh.asked).toEqual(['local.test'])
	// Nothing to get around, so no method is offered.
	await expect(panel.getByRole('button', { name: /in Open a Link/ })).toHaveCount(0)
	await zing.close()
	await doh.close()
})

test('a DNS block shows the advice and a button that opens the recommended method', async () => {
	const doh = await serveDoh(['1.2.3.4'])
	const zing = await launchWith(doh.url, { system: ['10.0.0.1'], tcp: true, tlsValid: true })
	const { surface, panel } = await openDiagnose(zing)
	await diagnose(panel, 'https://Blocked.test/some/page')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('DNS-level block')
	await expect(panel.getByTestId('diagnose-detail')).toContainText('DoH + IP pinning')
	await expect(panel.getByTestId('diagnose-recommended')).toHaveText('DoH + IP pinning, Reader relay')
	expect(doh.asked).toEqual(['blocked.test'])
	await panel.getByRole('button', { name: 'Open DoH + IP pinning in Open a Link' }).click()
	const opened = surface.getByRole('dialog', { name: 'Open a Link' })
	await expect(opened).toBeVisible()
	// The address as it was typed, path and all, is filled in; running it is a click there.
	await expect(opened.getByRole('textbox', { name: 'Address' })).toHaveValue('https://Blocked.test/some/page')
	await expect(opened.getByTestId('attempt')).toHaveCount(0)
	await zing.close()
	await doh.close()
})

test('encrypted DNS that the network blocks is explained in the report, not shown as an error', async () => {
	const zing = await launchWith(await deadAddress(), { system: ['1.2.3.4'], tcp: false, tlsValid: false })
	const { panel } = await openDiagnose(zing)
	await diagnose(panel, 'local.test')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('Inconclusive')
	await expect(steps(panel)).toHaveValue(/Public DoH: couldn't be used/)
	await expect(steps(panel)).toHaveValue(/block or rewrite encrypted DNS/)
	await expect(panel.getByRole('alert')).toHaveCount(0)
	await zing.close()
})

test('not blocked without encrypted DNS says the verdict rests on the certificate', async () => {
	const zing = await launchWith(await deadAddress(), { system: ['1.2.3.4'], tcp: true, tlsValid: true })
	const { panel } = await openDiagnose(zing)
	await diagnose(panel, 'local.test')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('Not blocked')
	await expect(panel.getByTestId('diagnose-caveat')).toContainText("Encrypted DNS couldn't be checked on this network")
	await zing.close()
})

test('a verdict that used encrypted DNS carries no caveat', async () => {
	const doh = await serveDoh(['93.184.216.34'])
	const zing = await launchWith(doh.url, { system: ['93.184.216.34'], tcp: true, tlsValid: true })
	const { panel } = await openDiagnose(zing)
	await diagnose(panel, 'local.test')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('Not blocked')
	await expect(panel.getByTestId('diagnose-caveat')).toHaveCount(0)
	await zing.close()
	await doh.close()
})

test('something that is not a site name is refused with a reason', async () => {
	const doh = await serveDoh(['1.2.3.4'])
	const zing = await launchWith(doh.url, { system: [], tcp: false, tlsValid: false })
	const { panel } = await openDiagnose(zing)
	await diagnose(panel, 'not a site')
	await expect(panel.getByRole('alert')).toContainText("isn't a site name")
	await expect(panel.getByTestId('diagnose-verdict')).toHaveCount(0)
	expect(doh.asked).toEqual([])
	await zing.close()
	await doh.close()
})

// ---- Relayed pages (X2). A local server stands in for every site, the reader relay and the archive:
// the host's network in this run is LocalRelayTransport (ZING_E2E_RELAY_URL), so nothing reaches the internet.

interface Fetched {
	readonly target: string
	readonly via: string
	readonly headers: IncomingMessage['headers']
}

interface Stand {
	readonly origin: string
	/** Every request the host's engine made on a relayed page's behalf. */
	readonly fetched: Fetched[]
	/** Requests that came from the browser itself, i.e. a web page, not from the engine. */
	readonly direct: string[]
	close(): Promise<void>
}

const noText = Array.from({ length: 25 }, () => '<script>0</script>').join('')

const sites: Readonly<Record<string, (path: string) => { type: string; body: string } | undefined>> = {
	'site.test': (path) => {
		if (path === '/a/page.html') {
			return {
				type: 'text/html; charset=utf-8',
				body: '<!doctype html><title>Relayed</title><link rel="stylesheet" href="style.css"><p id="t">hello</p><a id="abs" href="https://other.test/x">abs</a><img src="https://img.test/p.png">',
			}
		}
		if (path === '/a/style.css') return { type: 'text/css', body: 'body{background:rgb(1,2,3)}' }
		if (path === '/a/file.zip') return { type: 'application/zip', body: 'PK-not-really' }
		return undefined
	},
	'other.test': (path) => ({ type: 'text/html', body: `<!doctype html><title>other ${path}</title><p>other</p>` }),
	'app.test': () => ({ type: 'text/html', body: `<!doctype html><title>App</title>${noText}` }),
	'img.test': () => ({ type: 'image/png', body: 'x' }),
}

/** An ordinary web page that tries every way to reach the relay scheme. */
const attackPage = `<!doctype html><title>Attack</title>
<a id="link" href="zing-relay://site.test/a/page.html">link</a>
<iframe id="frame" src="zing-relay://site.test/a/page.html"></iframe>
<img id="img" src="zing-relay://site.test/a/style.css">
<form id="form" method="post" action="zing-relay://site.test/a/page.html"><button>go</button></form>`

async function standIn(): Promise<Stand> {
	const fetched: Fetched[] = []
	const direct: string[] = []
	const server: Server = createServer((request, response) => {
		const target = request.headers['x-relay-target']
		if (typeof target !== 'string') {
			direct.push(request.url ?? '')
			if (request.url === '/redirect-to-relay') {
				response.statusCode = 302
				response.setHeader('Location', 'zing-relay://site.test/a/page.html')
				response.end()
				return
			}
			if (request.url === '/redirect-ordinary') {
				response.statusCode = 302
				response.setHeader('Location', '/typed-after-redirect')
				response.end()
				return
			}
			response.setHeader('Content-Type', 'text/html')
			response.end(attackPage)
			return
		}
		const url = new URL(target)
		fetched.push({ target, via: String(request.headers['x-relay-via']), headers: request.headers })
		const served = sites[url.host]?.(url.pathname)
		if (!served) {
			response.statusCode = 404
			response.end('no such page')
			return
		}
		response.setHeader('Content-Type', served.type)
		response.end(served.body)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	const { port } = server.address() as AddressInfo
	return { origin: `http://127.0.0.1:${port}`, fetched, direct, close: () => new Promise((resolve) => server.close(() => resolve())) }
}

/** What a page's scripts reach, through globalThis since this file has no DOM types. */
interface PageGlobals {
	readonly document: {
		readonly body: unknown
		readonly title: string
		cookie: string
		getElementById(id: string): { click(): void; submit(): void; getAttribute(name: string): string }
	}
	readonly location: { href: string; origin: string; reload(): void }
	readonly chrome?: { webview?: { postMessage(m: unknown): void; hostObjects?: { sync?: Record<string, unknown> } } }
	readonly zing?: unknown
	open(url: string): unknown
	fetch(url: string): Promise<unknown>
	getComputedStyle(element: unknown): { backgroundColor: string }
}

const inPage = <T>(page: Page, run: (g: PageGlobals) => T): Promise<T> => page.evaluate(`(${run.toString()})(globalThis)`) as Promise<T>

async function launchRelayed(stand: Stand, env: Record<string, string> = {}): Promise<Launched> {
	return launchZing({ env: { ZING_E2E_RELAY_URL: stand.origin, ...env } })
}

async function hook(zing: Launched, message: Record<string, unknown>): Promise<void> {
	await zing.chrome.evaluate((m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', ...m }), message)
}

const tabsOf = (zing: Launched) => zing.chrome.evaluate(() => window.zing.getTabs())

async function openRelayed(zing: Launched, address: string): Promise<Page> {
	await hook(zing, { action: 'open-relayed', url: address })
	return pageWhere(zing.browser, (p) => p.url().startsWith('zing-relay://') && p.url().includes(new URL(address).host))
}

async function navigateActive(zing: Launched, address: string): Promise<void> {
	await zing.chrome.evaluate(async (to) => {
		const { activeId } = await window.zing.getTabs()
		await window.zing.navigateTab(activeId!, to)
	}, address)
}

/** The page's text, or nothing while the page is between documents. */
const textOf = (page: Page): Promise<string> => page.evaluate('document.body ? document.body.innerText : ""').then(String, () => '')

const settle = (ms = 800): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms))

test('a relayed page loads its stylesheet through the relay', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	await expect.poll(() => inPage(page, (g) => g.getComputedStyle(g.document.body).backgroundColor)).toBe('rgb(1, 2, 3)')
	// The tab says it is relayed, and the page has an origin of its own.
	expect((await tabsOf(zing)).tabs.some((t) => t.url === 'zing-relay://site.test/a/page.html')).toBe(true)
	expect(await inPage(page, (g) => g.location.origin)).toBe('zing-relay://site.test')
	expect(stand.fetched.find((f) => f.target === 'https://site.test/a/style.css')?.via).toBe('plain')
	// What an anonymous visitor named Zing sends: no cookie, no sign-in, not a browser's user agent.
	for (const f of stand.fetched) {
		expect(f.headers.cookie).toBeUndefined()
		expect(f.headers.authorization).toBeUndefined()
		expect(f.headers['user-agent']).toBe('Zing')
	}
	// Nothing is kept for the origin: a cookie its script sets is not stored.
	expect(await inPage(page, (g) => { g.document.cookie = 'a=1'; return g.document.cookie })).toBe('')
	await zing.close()
	await stand.close()
})

test('a relayed page is kept off the direct network and its links stay in the relay', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	await expect.poll(() => inPage(page, (g) => g.document.getElementById('abs').getAttribute('href'))).toBe('zing-relay://other.test/x')
	// The image on another host went through the relay too, not the network.
	await expect.poll(() => stand.fetched.map((f) => f.target)).toContain('https://img.test/p.png')
	// An https address a script builds at run time is steered into the relay.
	await page.evaluate("globalThis.location.href = 'https://other.test/runtime'")
	await expect.poll(() => pages(zing.browser).some((p) => p.url() === 'zing-relay://other.test/runtime')).toBe(true)
	const relayed = pages(zing.browser).find((p) => p.url() === 'zing-relay://other.test/runtime')!
	// Nor can it reach Zing's own pages, files or the network.
	for (const target of ['zing://home', 'file:///c:/windows/win.ini', `${stand.origin}/nav`]) {
		await relayed.evaluate(`globalThis.location.href = ${JSON.stringify(target)}`)
		await settle(300)
		expect(relayed.url()).toBe('zing-relay://other.test/runtime')
	}
	// Another view's origin is only an https address: it is steered into the relay like any other, where it is just a site that does not exist.
	await relayed.evaluate("globalThis.location.href = 'https://app.zing/index.html#chrome'")
	await expect.poll(() => pages(zing.browser).some((p) => p.url() === 'zing-relay://app.zing/index.html#chrome')).toBe(true)
	expect(stand.direct).toEqual([])
	await zing.close()
	await stand.close()
})

test('a relayed page cannot reach the network by fetch, image or script', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	const outcome = await page.evaluate(`(async () => {
		const direct = ${JSON.stringify(stand.origin)}
		const seen = {}
		seen.fetch = await fetch(direct + '/fetch').then(() => 'read', (e) => e.name)
		seen.noCors = await fetch(direct + '/no-cors', { mode: 'no-cors' }).then(() => 'sent', (e) => e.name)
		new Image().src = direct + '/image'
		const script = document.createElement('script')
		script.src = direct + '/script'
		document.head.append(script)
		await new Promise((resolve) => setTimeout(resolve, 800))
		return seen
	})()`)
	// The content security policy on the relay's responses refuses these, so no request leaves the page.
	expect(outcome).toEqual({ fetch: 'TypeError', noCors: 'TypeError' })
	expect(stand.direct).toEqual([])
	await zing.close()
	await stand.close()
})

test('a relayed page reads its own origin’s files but not another relay host’s', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	const outcome = await page.evaluate(`(async () => ({
		same: await fetch('zing-relay://site.test/a/style.css').then((r) => r.text(), (e) => e.name),
		other: await fetch('zing-relay://other.test/x').then(() => 'read', (e) => e.name),
	}))()`)
	expect(outcome).toEqual({ same: 'body{background:rgb(1,2,3)}', other: 'TypeError' })
	await zing.close()
	await stand.close()
})

test('a relayed page’s iframe and popup stay in the relay', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	await page.evaluate(`(() => {
		for (const src of ['zing-relay://other.test/frame', ${JSON.stringify(stand.origin + '/frame')}, 'zing://home', 'file:///c:/windows/win.ini']) {
			const frame = document.createElement('iframe')
			frame.src = src
			document.body.append(frame)
		}
	})()`)
	await expect.poll(() => page.frames().some((f) => f.url() === 'zing-relay://other.test/frame')).toBe(true)
	await settle()
	expect(page.frames().filter((f) => f !== page.mainFrame() && !/^(zing-relay:\/\/|about:|chrome-error:|$)/.test(f.url())).map((f) => f.url())).toEqual([])
	// Popups: a relayed one opens as a relayed tab, and an https one is mapped onto the relay like the page's links; a web one and Zing's own open nothing.
	const before = (await tabsOf(zing)).tabs.length
	await page.evaluate("globalThis.open(" + JSON.stringify(stand.origin + '/pop') + "); globalThis.open('zing://home'); globalThis.open('file:///c:/windows/win.ini')")
	await settle()
	expect((await tabsOf(zing)).tabs.length).toBe(before)
	await page.evaluate("globalThis.open('zing-relay://other.test/pop')")
	await expect.poll(async () => (await tabsOf(zing)).tabs.some((t) => t.url === 'zing-relay://other.test/pop')).toBe(true)
	await page.evaluate("globalThis.open('https://nowhere.test/')")
	await expect.poll(async () => (await tabsOf(zing)).tabs.some((t) => t.url === 'zing-relay://nowhere.test/')).toBe(true)
	expect(stand.direct).toEqual([])
	await zing.close()
	await stand.close()
})

test('a relayed page posting to the host changes nothing and no renderer receives it', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	const id = (await tabsOf(zing)).tabs.find((t) => t.url.startsWith('zing-relay://site.test'))!.id
	// What the chrome would act on (and would close the tab for) if it were delivered to the bridge.
	const seen = (await page.evaluate(`(() => {
		const w = globalThis
		const result = { webview: typeof w.chrome?.webview, zing: typeof w.zing, post: '', hostObject: '' }
		try { w.chrome.webview.postMessage({ kind: 'invoke', id: 1, channel: 'tabs:close', args: [${JSON.stringify(id)}] }); result.post = 'returned' } catch (e) { result.post = 'threw ' + e.name }
		try { result.hostObject = typeof w.chrome.webview.hostObjects.sync.zing } catch (e) { result.hostObject = 'threw ' + e.name }
		return result
	})()`)) as { zing: string; hostObject: string }
	console.log('a relayed page sees', JSON.stringify(seen))
	expect(seen).toMatchObject({ zing: 'undefined' })
	expect(seen.hostObject).not.toBe('object')
	await settle()
	// Still open and still the same page; and no renderer got a reply or an event (an unhandled error would fail close()).
	expect((await tabsOf(zing)).tabs.some((t) => t.id === id)).toBe(true)
	await zing.close()
	await stand.close()
})

test('a web page cannot reach the relay scheme by any route', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	await navigateActive(zing, `${stand.origin}/attack`)
	const web = await pageWhere(zing.browser, (p) => p.url() === `${stand.origin}/attack`)
	await settle()
	// Frame and subresource: the iframe never loads the relay page, and the image is refused.
	expect(web.frames().filter((f) => f.url().startsWith('zing-relay://'))).toEqual([])
	// A link click, location, window.open, a form post and a fetch.
	await web.evaluate("document.getElementById('link').click()")
	await web.evaluate("globalThis.location.href = 'zing-relay://site.test/a/page.html'").catch(() => undefined)
	await web.evaluate("globalThis.open('zing-relay://site.test/a/page.html')")
	await web.evaluate("document.getElementById('form').submit()").catch(() => undefined)
	await web.evaluate("globalThis.fetch('zing-relay://site.test/a/page.html').catch(() => 'blocked')").catch(() => undefined)
	await settle()
	expect(pages(zing.browser).some((p) => p.url().startsWith('zing-relay://'))).toBe(false)
	// A redirect from an http page.
	await navigateActive(zing, `${stand.origin}/redirect-to-relay`)
	await settle()
	// What matters: Zing fetched nothing for any of it, and no tab became a relayed one.
	expect(stand.fetched).toEqual([])
	expect((await tabsOf(zing)).tabs.some((t) => t.url.startsWith('zing-relay://'))).toBe(false)
	expect(pages(zing.browser).some((p) => p.url().startsWith('zing-relay://'))).toBe(false)
	await zing.close()
	await stand.close()
})

test('a relayed tab wakes from sleep as a relayed page, because Zing starts that navigation', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand, { ZING_TEST_CLOCK: '1' })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	await openRelayed(zing, 'https://site.test/a/page.html')
	const id = (await tabsOf(zing)).tabs.find((t) => t.url.startsWith('zing-relay://site.test'))!.id
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))
	await hook(zing, { action: 'advance-clock', ms: 3 * 60_000 })
	await expect.poll(async () => (await tabsOf(zing)).tabs.find((t) => t.id === id)?.sleeping).toBe(true)
	await expect.poll(() => pages(zing.browser).some((p) => p.url().startsWith('zing-relay://'))).toBe(false)
	await zing.chrome.evaluate((tab) => window.zing.activateTab(tab), id)
	const woken = await pageWhere(zing.browser, (p) => p.url() === 'zing-relay://site.test/a/page.html')
	await expect.poll(() => inPage(woken, (g) => g.document.title)).toBe('Relayed')
	await zing.close()
	await stand.close()
})

test('an app-like page says it can\u2019t work here', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	await openRelayed(zing, 'https://app.test/')
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(overlay.getByText(/app\.test is a web app, and web apps can't work through the relay/)).toBeVisible()
	await zing.close()
	await stand.close()
})

test('without the session\u2019s OK a blocked page is not sent to a third party, and with it the archive is asked', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://nowhere.test/gone')
	const hosts = () => stand.fetched.map((f) => new URL(f.target).host)
	await expect.poll(() => inPage(page, (g) => (g.document.body as { innerText: string }).innerText)).toContain('Needs your OK')
	expect(hosts()).not.toContain('r.jina.ai')
	expect(hosts()).not.toContain('archive.org')
	await hook(zing, { action: 'relay-consent' })
	await page.evaluate('globalThis.location.reload()')
	await expect.poll(hosts).toContain('archive.org')
	await zing.close()
	await stand.close()
})

test('an address typed in a relayed tab still opens as an ordinary page, and Back returns to the relayed one', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	await openRelayed(zing, 'https://site.test/a/page.html')
	await navigateActive(zing, `${stand.origin}/typed`)
	const web = await pageWhere(zing.browser, (p) => p.url() === `${stand.origin}/typed`)
	await expect.poll(() => inPage(web, (g) => g.document.title)).toBe('Attack')
	expect(stand.direct.filter((u) => u !== '/favicon.ico')).toEqual(['/typed'])
	await zing.chrome.evaluate(() => window.zing.runCommand('history.back'))
	await pageWhere(zing.browser, (p) => p.url() === 'zing-relay://site.test/a/page.html')
	await zing.close()
	await stand.close()
})

test('a relayed page cannot reach loopback or the own network through the relay', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	await page.evaluate(`(async () => {
		for (const address of ['zing-relay://127.0.0.1/', 'zing-relay://localhost/', 'zing-relay://192.168.1.1/admin', 'zing-relay://printer.local/', 'zing-relay://nas.lan/', 'zing-relay://100.64.0.1/'])
			await fetch(address).catch(() => undefined)
	})()`)
	await page.evaluate("setTimeout(() => { globalThis.location.href = 'zing-relay://127.0.0.1/' }, 0)")
	await expect.poll(() => textOf(page)).toContain('your own network')
	// Opened by Zing as a relayed tab too.
	const own = await openRelayed(zing, 'https://localhost/')
	await expect.poll(() => textOf(own)).toContain('your own network')
	const hosts = stand.fetched.map((f) => new URL(f.target).host)
	for (const forbidden of ['127.0.0.1', 'localhost', '192.168.1.1', 'printer.local', 'nas.lan', '100.64.0.1']) expect(hosts).not.toContain(forbidden)
	await zing.close()
	await stand.close()
})

// ---- Round 2 trials: each was written to find out whether the suspicion is real.

const realRequests = (stand: Stand): string[] => stand.direct.filter((u) => u !== '/favicon.ico')

test('a popup of a relayed page cannot be sent to the network before it has loaded', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	await page.evaluate(`(async () => {
		const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
		const w = window.open('zing-relay://other.test/pop')
		try { w.location = ${JSON.stringify(stand.origin + '/loc')} } catch (e) {}
		try { w.document.write('<p>x</p>') } catch (e) {}
		try { w.location.href = ${JSON.stringify(stand.origin + '/href')} } catch (e) {}
		const v = window.open('about:blank')
		try { v.location = ${JSON.stringify(stand.origin + '/blank')} } catch (e) {}
		await sleep(300)
		try { w.location = ${JSON.stringify(stand.origin + '/late')} } catch (e) {}
		await sleep(1500)
	})()`)
	await settle()
	expect(realRequests(stand)).toEqual([])
	await zing.close()
	await stand.close()
})

test('a download link on a relayed page cannot fetch from the network', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	await page.evaluate(`(() => {
		for (const href of ['zing-relay://site.test/a/style.css?dl=1', ${JSON.stringify(stand.origin + '/dl')}]) {
			const a = document.createElement('a')
			a.href = href
			a.download = 'f.bin'
			document.body.append(a)
			a.click()
		}
	})()`)
	await settle(2000)
	console.log('download trial: url', page.url(), 'direct', JSON.stringify(stand.direct), 'fetched', JSON.stringify(stand.fetched.map((f) => f.target)))
	console.log('downloads on disk', JSON.stringify(existsSync(join(zing.userDataDir, 'downloads')) ? readdirSync(join(zing.userDataDir, 'downloads')) : 'no folder'))
	expect(realRequests(stand)).toEqual([])
	await zing.close()
	await stand.close()
})

test('a relay address with a fragment loads its content', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const relayed = await openRelayed(zing, 'https://site.test/a/page.html#sec')
	await expect.poll(() => inPage(relayed, (g) => g.document.title)).toBe('Relayed')
	await zing.close()
	await stand.close()
})

test('a typed address with a fragment loads from a relayed tab', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	await openRelayed(zing, 'https://site.test/a/page.html')
	await navigateActive(zing, `${stand.origin}/attack#sec`)
	const web = await pageWhere(zing.browser, (p) => p.url() === `${stand.origin}/attack#sec`)
	await expect.poll(() => inPage(web, (g) => g.document.title)).toBe('Attack')
	await zing.close()
	await stand.close()
})

test('a typed address that redirects loads from a relayed tab', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	await openRelayed(zing, 'https://site.test/a/page.html')
	await navigateActive(zing, `${stand.origin}/redirect-ordinary`)
	const landed = await pageWhere(zing.browser, (p) => p.url() === `${stand.origin}/typed-after-redirect`)
	await expect.poll(() => inPage(landed, (g) => g.document.title)).toBe('Attack')
	await zing.close()
	await stand.close()
})

test('a file link on a relayed page downloads the bytes the relay fetched', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	await page.evaluate("setTimeout(() => { const a = document.createElement('a'); a.href = 'file.zip'; document.body.append(a); a.click() }, 0)")
	const folder = join(zing.userDataDir, 'downloads')
	await expect.poll(() => (existsSync(folder) ? readdirSync(folder) : [])).toContain('file.zip')
	expect(stand.fetched.map((f) => f.target)).toContain('https://site.test/a/file.zip')
	expect(realRequests(stand)).toEqual([])
	await zing.close()
	await stand.close()
})

// ---- Open a Link (X1) and the offer after a failed load. A local server answers for every site, the reader relay and the
// archive, per method, and records what the host asked it: nothing reaches the internet.

interface Asked {
	readonly target: string
	readonly via: string
}

interface Reply {
	readonly status?: number
	readonly body?: string
	readonly type?: string
	/** Cuts the connection, as a filter's reset would. */
	readonly cut?: boolean
	/** Waits for this before answering. */
	readonly after?: Promise<void>
}

interface Methods {
	readonly asked: Asked[]
	readonly origin: string
	close(): Promise<void>
}

/** `answer` decides per request what the host's engine gets; a page the browser itself asks for is a plain page. */
async function methodsStand(answer: (target: URL, via: string) => Reply): Promise<Methods> {
	const asked: Asked[] = []
	const server: Server = createServer((request, response) => {
		const target = request.headers['x-relay-target']
		if (typeof target !== 'string') {
			response.setHeader('Content-Type', 'text/html')
			response.end('<!doctype html><title>Plain</title><p>plain</p>')
			return
		}
		const via = String(request.headers['x-relay-via'])
		asked.push({ target, via })
		const reply = answer(new URL(target), via)
		void (reply.after ?? Promise.resolve()).then(() => {
			if (reply.cut) {
				response.socket?.destroy()
				return
			}
			response.statusCode = reply.status ?? 200
			response.setHeader('Content-Type', reply.type ?? 'text/html')
			response.end(reply.body ?? '<!doctype html><title>Served</title><p>served</p>')
		})
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	const { port } = server.address() as AddressInfo
	return { asked, origin: `http://127.0.0.1:${port}`, close: () => new Promise((resolve) => server.close(() => resolve())) }
}

/** The site is blocked for the engine's own two methods; the reader relay answers and the archive has nothing. */
const blockedSite = (target: URL, via: string): Reply => {
	if (target.host === 'r.jina.ai') return { type: 'text/plain', body: 'Title\n\nreader text '.repeat(10) }
	if (target.host === 'archive.org') return { status: 404 }
	return via === 'plain' || via === 'pinned' ? { cut: true } : { status: 404 }
}

const thirdPartyAsked = (stand: Methods): Asked[] => stand.asked.filter((a) => ['r.jina.ai', 'archive.org'].includes(new URL(a.target).host))

/** The host's network is the stand; ZING_E2E_OFFLINE makes every page load of the engine fail, as a block would. */
async function launchOffline(stand: Methods): Promise<Launched> {
	return launchZing({ env: { ZING_E2E_RELAY_URL: stand.origin, ZING_E2E_OFFLINE: '1' } })
}

async function launchOnStand(stand: Methods): Promise<Launched> {
	return launchZing({ env: { ZING_E2E_RELAY_URL: stand.origin } })
}

/** No name resolves in the engine, as when a network's DNS blocks them: a page load fails with HostNameNotResolved. */
async function launchWithoutDns(stand: Methods): Promise<Launched> {
	return launchZing({ env: { ZING_E2E_RELAY_URL: stand.origin, ZING_E2E_NO_DNS: '1' } })
}

async function openLinkPanel(zing: Launched): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').filter({ hasText: 'Open a Link' }).click()
	const panel = surface.getByRole('dialog', { name: 'Open a Link' })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

const tabUrls = (zing: Launched): Promise<string[]> => tabsOf(zing).then((state) => state.tabs.map((t) => t.url))

async function runLink(panel: Locator, address: string): Promise<void> {
	await panel.getByRole('textbox', { name: 'Address' }).fill(address)
	await panel.getByRole('button', { name: 'Open', exact: true }).click()
}

const consentSentence =
	"This asks an outside service to fetch the page for you, so that service sees the address, and for a reader relay the page too. Files the page you open asks for may go the same way. Zing doesn't send it your cookies or sign-ins. If this network belongs to a school or employer, getting around its filter may break their rules. That's your call."

test('a failed load offers Open a Link and Diagnose, and nothing runs without a click', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchWithoutDns(stand)
	const address = 'https://blocked.test/a/page'
	await navigateActive(zing, address)
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	const offer = overlay.getByTestId('unblock-offer')
	await expect(offer).toBeVisible()
	await expect(offer.getByTestId('offer-address')).toHaveText(address)
	await expect(offer.getByRole('button', { name: 'Open a Link' })).toBeVisible()
	await expect(offer.getByRole('button', { name: 'Diagnose' })).toBeVisible()
	// Left alone, it stays an offer: neither the failure nor the offer made a request.
	await settle(1500)
	expect(stand.asked).toEqual([])
	await offer.getByRole('button', { name: 'Open a Link' }).click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	const panel = surface.getByRole('dialog', { name: 'Open a Link' })
	await expect(panel.getByRole('textbox', { name: 'Address' })).toHaveValue(address)
	await expect(offer).toBeHidden()
	// Opened with the address in, and still nothing tried: it takes a click in the tool.
	await settle(1000)
	expect(stand.asked).toEqual([])
	await expect(panel.getByTestId('attempt')).toHaveCount(0)
	await panel.getByRole('button', { name: 'Open', exact: true }).click()
	await expect.poll(() => stand.asked.length).toBeGreaterThan(0)
	await zing.close()
	await stand.close()
})

test('the offer opens Diagnose with the address in and runs nothing either', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchWithoutDns(stand)
	const address = 'https://blocked.test/'
	await navigateActive(zing, address)
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await overlay.getByTestId('unblock-offer').getByRole('button', { name: 'Diagnose' }).click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	const panel = surface.getByRole('dialog', { name: 'Diagnose' })
	await expect(panel.getByRole('textbox', { name: 'Site' })).toHaveValue(address)
	await settle(800)
	await expect(panel.getByTestId('diagnose-verdict')).toHaveCount(0)
	expect(stand.asked).toEqual([])
	await zing.close()
	await stand.close()
})

test('a failure that is not like a block gets the plain notice and no offer', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchOffline(stand)
	await navigateActive(zing, 'https://blocked.test/a/page')
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(overlay.getByRole('status')).toContainText("Couldn't load https://blocked.test/a/page")
	await expect(overlay.getByTestId('unblock-offer')).toHaveCount(0)
	expect(stand.asked).toEqual([])
	await zing.close()
	await stand.close()
})

test('the first third-party method asks once per session and says why', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchOffline(stand)
	const { panel } = await openLinkPanel(zing)
	await runLink(panel, 'https://blocked.test/a/page')
	const dialog = panel.getByRole('alertdialog')
	await expect(dialog).toBeVisible()
	await expect(dialog).toContainText(consentSentence)
	// Focus lands on it, and the methods that need no outside service were already tried, in order, with their results.
	await expect.poll(() => dialog.evaluate((el) => el.contains(document.activeElement))).toBe(true)
	const rows = panel.getByTestId('attempt')
	await expect(rows).toHaveCount(4)
	await expect(rows.nth(0)).toHaveAttribute('data-strategy', 'direct')
	await expect(rows.nth(0)).toHaveAttribute('data-status', 'failed')
	await expect(rows.nth(1)).toHaveAttribute('data-strategy', 'dohPinned')
	await expect(rows.nth(1)).toHaveAttribute('data-status', 'failed')
	// A filter cutting encrypted DNS is a usual result, and the wording says so.
	await expect(panel.getByTestId('pinned-note')).toContainText('common')
	await expect(rows.nth(2)).toHaveAttribute('data-status', 'skipped')
	expect(thirdPartyAsked(stand)).toEqual([])

	await dialog.getByRole('button', { name: 'Allow for this session' }).click()
	await expect(dialog).toBeHidden()
	await expect.poll(() => thirdPartyAsked(stand).length).toBeGreaterThan(0)
	await expect(panel.getByTestId('open-link-outcome')).toContainText('through the relay')
	await expect(rows.nth(2)).toHaveAttribute('data-status', 'worked')
	// The page opened in a tab of its own, relayed (an ordinary tab would meet the same block).
	await expect.poll(() => tabUrls(zing)).toContain('zing-relay://blocked.test/a/page')

	// A second address in the same session goes straight through: it does not ask again.
	await runLink(panel, 'https://also-blocked.test/')
	await expect.poll(() => tabUrls(zing)).toContain('zing-relay://also-blocked.test/')
	await expect(panel.getByRole('alertdialog')).toHaveCount(0)
	await zing.close()
	await stand.close()
})

test('declining stops at the methods that need no third party', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchOffline(stand)
	const { panel } = await openLinkPanel(zing)
	await expect(panel.getByTestId('doh-notice')).toContainText("Cloudflare receives the site's name")
	const before = await tabUrls(zing)
	await runLink(panel, 'https://blocked.test/a/page')
	const dialog = panel.getByRole('alertdialog')
	await expect(dialog).toBeVisible()
	// Escape declines.
	await panel.page().keyboard.press('Escape')
	await expect(dialog).toBeHidden()
	await expect(panel.getByTestId('open-link-stopped')).toContainText('Cloudflare had already been asked for the site')
	await expect(panel.getByTestId('open-link-stopped')).toContainText('Nothing was sent to the reader relay or the archive.')
	await settle(800)
	expect(thirdPartyAsked(stand)).toEqual([])
	expect(await tabUrls(zing)).toEqual(before)
	// Declining did not grant anything: the next try asks again, and the Don't allow button declines too.
	await panel.getByRole('button', { name: 'Open', exact: true }).click()
	await expect(dialog).toBeVisible()
	await dialog.getByRole('button', { name: "Don't allow" }).click()
	await expect(dialog).toBeHidden()
	await settle(500)
	expect(thirdPartyAsked(stand)).toEqual([])
	await zing.close()
	await stand.close()
})

test('a page that is reachable directly opens in an ordinary tab', async () => {
	const stand = await methodsStand(() => ({ body: '<!doctype html><title>Fine</title><p>fine</p>' }))
	const zing = await launchOnStand(stand)
	const { panel } = await openLinkPanel(zing)
	await runLink(panel, `${stand.origin}/fine`)
	await expect(panel.getByTestId('open-link-outcome')).toContainText('loads as usual')
	await expect(panel.getByTestId('attempt')).toHaveCount(1)
	await expect.poll(() => tabUrls(zing)).toContain(`${stand.origin}/fine`)
	expect(stand.asked.map((a) => a.via)).toEqual(['plain'])
	await zing.close()
	await stand.close()
})

test('a run that ends after its panel was closed opens nothing', async () => {
	let release: () => void = () => undefined
	const held = new Promise<void>((resolve) => {
		release = resolve
	})
	const stand = await methodsStand(() => ({ after: held }))
	const zing = await launchOnStand(stand)
	const { panel } = await openLinkPanel(zing)
	const before = await tabUrls(zing)
	await runLink(panel, `${stand.origin}/slow`)
	await expect.poll(() => stand.asked.length).toBe(1)
	await panel.getByRole('button', { name: 'Close Open a Link' }).click()
	await expect(panel).toBeHidden()
	release()
	await settle(1500)
	expect(await tabUrls(zing)).toEqual(before)
	await zing.close()
	await stand.close()
})

test('an http address that needed a relay is tried once over https, else said plainly', async () => {
	const stand = await methodsStand((target, via) => {
		// plain.test answers over either scheme; nowhere.test only as http, which a relayed tab can't show.
		const reads = target.pathname.includes('://plain.test/') || target.pathname.startsWith('/http://nowhere.test/')
		if (target.host === 'r.jina.ai') return reads ? { type: 'text/plain', body: 'text '.repeat(20) } : { status: 404 }
		return via === 'plain' || via === 'pinned' ? { cut: true } : { status: 404 }
	})
	const zing = await launchOffline(stand)
	const { panel } = await openLinkPanel(zing)
	// The first yes, then an http address that only the https form of can be relayed.
	await runLink(panel, 'http://plain.test/x')
	await panel.getByRole('alertdialog').getByRole('button', { name: 'Allow for this session' }).click()
	await expect.poll(() => tabUrls(zing)).toContain('zing-relay://plain.test/x')
	expect((await tabUrls(zing)).some((u) => u.startsWith('http://plain.test'))).toBe(false)
	// How the http address went stays in view beside the https try.
	await expect(panel.getByRole('list', { name: 'Attempts over http' }).getByTestId('attempt').first()).toBeVisible()
	await expect(panel.getByRole('list', { name: 'Attempts', exact: true })).toBeVisible()
	// Another one has no https form that answers: nothing opens, and the message says why.
	const before = await tabUrls(zing)
	await runLink(panel, 'http://nowhere.test/y')
	await expect(panel.getByRole('alert')).toContainText("can't be relayed")
	await expect(panel.getByRole('alert')).toContainText('http://nowhere.test/y')
	expect(await tabUrls(zing)).toEqual(before)
	await zing.close()
	await stand.close()
})

test('a relayed page cannot grant the third-party consent', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	// A relayed page has no bridge, and a message it posts to the host is refused.
	expect(await inPage(page, (g) => g.zing)).toBeUndefined()
	await page.evaluate(`globalThis.chrome?.webview?.postMessage(${JSON.stringify({ kind: 'invoke', id: 1, channel: 'relay:allow-third-parties', args: [] })})`)
	await settle(500)
	const asked = await zing.chrome.evaluate(() => window.zing.openLink('example.com', 'after-the-page-tried'))
	expect(asked.outcome).toBe('needsConsent')
	await zing.close()
	await stand.close()
})

// ---- Fix round 1: the offer and the tool by keyboard, a full shell, Escape, handoffs while asking, and the https retry's rows.

async function failedLoad(zing: Launched, address: string): Promise<{ overlay: Page; offer: Locator }> {
	await navigateActive(zing, address)
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	const offer = overlay.getByTestId('unblock-offer')
	await expect(offer.getByTestId('offer-address')).toHaveText(address)
	return { overlay, offer }
}

test('the offer is a status a screen reader hears, and says what is on offer', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchWithoutDns(stand)
	const { overlay } = await failedLoad(zing, 'https://blocked.test/a')
	const status = overlay.getByRole('status')
	await expect(status).toContainText("This page didn't load")
	await expect(status).toContainText('Open a Link tries other ways to reach it')
	await expect(status).toContainText('Nothing runs until you choose')
	await zing.close()
	await stand.close()
})

test('with the offer showing, a tool picked by keyboard from the sheet starts with the failed address and runs nothing', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchWithoutDns(stand)
	await failedLoad(zing, 'https://blocked.test/a')
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.getByRole('searchbox', { name: 'Search tools' })).toBeFocused()
	await surface.getByRole('searchbox', { name: 'Search tools' }).fill('open a link')
	await surface.keyboard.press('Enter')
	const panel = surface.getByRole('dialog', { name: 'Open a Link' })
	await expect(panel.getByRole('textbox', { name: 'Address' })).toHaveValue('https://blocked.test/a')
	await settle(800)
	expect(stand.asked).toEqual([])
	await expect(panel.getByTestId('attempt')).toHaveCount(0)
	// Once the offer is dismissed, the tool starts empty.
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await overlay.getByTestId('unblock-offer').getByRole('button', { name: 'Dismiss' }).click()
	await panel.getByRole('button', { name: 'Close Open a Link' }).click()
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	await surface.getByRole('searchbox', { name: 'Search tools' }).fill('open a link')
	await surface.keyboard.press('Enter')
	await expect(surface.getByRole('dialog', { name: 'Open a Link' }).getByRole('textbox', { name: 'Address' })).toHaveValue('')
	await zing.close()
	await stand.close()
})

test('with three tools open the offer stays and says to close one', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchWithoutDns(stand)
	const { offer } = await failedLoad(zing, 'https://blocked.test/a')
	for (const content of ['tool:Hashes', 'tool:HMAC', 'tool:JSON']) {
		await zing.chrome.evaluate((id) => window.zing.openPanel(id, { x: 400, y: 300 }), content)
	}
	await offer.getByRole('button', { name: 'Open a Link' }).click()
	await expect(offer.getByTestId('offer-detail')).toHaveText('Three tools are already open. Close one, then try again.')
	await expect(offer).toBeVisible()
	await zing.close()
	await stand.close()
})

test('Escape declines even after a click on the dialog text moved focus off it', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchWithoutDns(stand)
	const { panel } = await openLinkPanel(zing)
	await runLink(panel, 'https://blocked.test/a/page')
	const dialog = panel.getByRole('alertdialog')
	await expect(dialog).toBeVisible()
	await dialog.getByText('That’s your call.'.replace('’', "'")).click()
	await expect.poll(() => dialog.evaluate((el) => el.contains(document.activeElement))).toBe(false)
	await panel.page().keyboard.press('Escape')
	await expect(dialog).toBeHidden()
	expect(thirdPartyAsked(stand)).toEqual([])
	await zing.close()
	await stand.close()
})

test('an address handed over while the question is open waits, and Allow runs the address that was asked about', async () => {
	const stand = await methodsStand(blockedSite)
	const zing = await launchWithoutDns(stand)
	const { panel } = await openLinkPanel(zing)
	await runLink(panel, 'https://typed.test/')
	const dialog = panel.getByRole('alertdialog')
	await expect(dialog).toBeVisible()
	const { overlay } = await failedLoad(zing, 'https://blocked.test/other')
	await overlay.getByTestId('unblock-offer').getByRole('button', { name: 'Open a Link' }).click()
	await settle(500)
	await expect(panel.getByRole('textbox', { name: 'Address' })).toHaveValue('https://typed.test/')
	await dialog.getByRole('button', { name: 'Allow for this session' }).click()
	await expect.poll(() => tabUrls(zing)).toContain('zing-relay://typed.test/')
	expect(stand.asked.some((a) => a.target.includes('blocked.test/other'))).toBe(false)
	// Then the handed address fills the field.
	await expect(panel.getByRole('textbox', { name: 'Address' })).toHaveValue('https://blocked.test/other')
	await zing.close()
	await stand.close()
})

// Network & VPN: the encrypted-DNS script. The host's DNS is a stub in every test run (Zing.Host/StubDnsSystem): no test reads
// a real adapter, shows an admin prompt or changes the PC's DNS. What the stub was asked to run goes to dns-ran.jsonl in the profile.

async function openNetwork(zing: Launched): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').filter({ hasText: 'Network & VPN' }).click()
	const panel = surface.getByRole('dialog', { name: 'Network & VPN' })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

const dnsScript = (panel: Locator): Locator => panel.getByRole('textbox', { name: 'Script that runs as administrator' })

const dnsRan = (zing: Launched): string[] => {
	const file = join(zing.userDataDir, 'dns-ran.jsonl')
	if (!existsSync(file)) return []
	return readFileSync(file, 'utf8')
		.trim()
		.split('\n')
		.map((line) => (JSON.parse(line) as { script: string }).script)
}

const dnsBackup = (zing: Launched): string => join(zing.userDataDir, 'dns-before.json')

test('the script is shown before it can run', async () => {
	const zing = await launchZing()
	const { panel } = await openNetwork(zing)
	await expect(dnsScript(panel)).toHaveValue(/-ceq 'Ethernet'[^]*?-ServerAddresses '1\.1\.1\.1', '1\.0\.0\.1', '2606:4700:4700::1111', '2606:4700:4700::1001'/)
	await expect(dnsScript(panel)).toHaveValue(/-ceq 'Wi-Fi'[^]*?-ServerAddresses '1\.1\.1\.1', '1\.0\.0\.1'\n/)
	await expect(panel.getByTestId('dns-adapters')).toHaveText('Ethernet, Wi-Fi')
	await expect(panel.getByRole('button', { name: 'Apply', exact: true })).toBeEnabled()
	// The page says what it did not manage, and what it keeps.
	await expect(panel.getByTestId('dns-engine-note')).toContainText("pages use Windows' DNS")
	await expect(panel.getByTestId('dns-keeps-note')).toContainText('until you Undo')
	// Opening the page read the adapters and nothing else: no script ran and nothing was saved.
	expect(dnsRan(zing)).toEqual([])
	expect(existsSync(dnsBackup(zing))).toBe(false)
	await zing.close()
})

test('Apply then Undo returns the page to its first state', async () => {
	const zing = await launchZing()
	const { panel } = await openNetwork(zing)
	const first = await dnsScript(panel).inputValue()
	await panel.getByRole('button', { name: 'Apply', exact: true }).click()
	await expect(panel.getByTestId('dns-notice')).toContainText('Encrypted DNS is set up on')
	// Exactly the script that was on show ran, and the old settings are kept for Undo.
	expect(dnsRan(zing)).toEqual([first])
	expect(JSON.parse(readFileSync(dnsBackup(zing), 'utf8')).adapters[0]).toMatchObject({ name: 'Ethernet', v4: ['10.0.0.1'] })
	await expect(panel.getByRole('button', { name: 'Apply', exact: true })).toHaveCount(0)
	await expect(dnsScript(panel)).toHaveValue(/-ceq 'Ethernet'[^]*?-ServerAddresses '10\.0\.0\.1'/)
	const undoScript = await dnsScript(panel).inputValue()
	await panel.getByRole('button', { name: 'Undo', exact: true }).click()
	await expect(panel.getByTestId('dns-notice')).toContainText('were put back')
	expect(dnsRan(zing)).toEqual([first, undoScript])
	expect(existsSync(dnsBackup(zing))).toBe(false)
	await expect(panel.getByRole('button', { name: 'Undo', exact: true })).toHaveCount(0)
	await expect(dnsScript(panel)).toHaveValue(first)
	await zing.close()
})

test('saying no to the admin prompt is "Nothing changed", not an error', async () => {
	const zing = await launchZing()
	const { panel } = await openNetwork(zing)
	await hook(zing, { action: 'dns-stub', run: 'declined' })
	await panel.getByRole('button', { name: 'Apply', exact: true }).click()
	await expect(panel.getByTestId('dns-notice')).toContainText('Nothing changed')
	await expect(panel.getByRole('alert')).toHaveCount(0)
	await expect(panel.getByRole('button', { name: 'Apply', exact: true })).toBeEnabled()
	expect(existsSync(dnsBackup(zing))).toBe(false)
	await zing.close()
})

test('a script that fails says so, and Undo stays available because it may have changed some adapters', async () => {
	const zing = await launchZing()
	const { panel } = await openNetwork(zing)
	await hook(zing, { action: 'dns-stub', run: 'fail' })
	await panel.getByRole('button', { name: 'Apply', exact: true }).click()
	await expect(panel.getByRole('alert')).toContainText('The script failed (exit code 3)')
	await expect(panel.getByTestId('dns-notice')).toHaveCount(0)
	await expect(panel.getByRole('button', { name: 'Undo', exact: true })).toBeEnabled()
	expect(existsSync(dnsBackup(zing))).toBe(true)
	await zing.close()
})

test('adapters that changed after the script was shown give a new script to read, and run nothing', async () => {
	const zing = await launchZing()
	const { panel } = await openNetwork(zing)
	const shown = await dnsScript(panel).inputValue()
	const adapters = { adapters: [{ name: 'Docking station', v4: '', v6enabled: false, v6: null }], doh: [], dohSupported: true }
	await hook(zing, { action: 'dns-stub', adapters: JSON.stringify(adapters) })
	await panel.getByRole('button', { name: 'Apply', exact: true }).click()
	await expect(panel.getByTestId('dns-notice')).toContainText('The script changed since it was shown')
	await expect(dnsScript(panel)).toHaveValue(/'Docking station'/)
	expect(dnsRan(zing)).toEqual([])
	expect(existsSync(dnsBackup(zing))).toBe(false)
	// A second click runs the script that is now on show, not the first.
	const second = await dnsScript(panel).inputValue()
	expect(second).not.toBe(shown)
	await panel.getByRole('button', { name: 'Apply', exact: true }).click()
	await expect(panel.getByTestId('dns-notice')).toContainText('Encrypted DNS is set up on')
	expect(dnsRan(zing)).toEqual([second])
	await zing.close()
})

test('saved settings that can not be read are a named error naming the file', async () => {
	const userDataDir = scratchDir('zing-host-data-')
	writeFileSync(join(userDataDir, 'dns-before.json'), '{ not json')
	const zing = await launchZing({ userDataDir })
	const { panel } = await openNetwork(zing)
	await expect(panel.getByRole('alert')).toContainText('dns-before.json')
	await expect(panel.getByRole('button', { name: 'Apply', exact: true })).toHaveCount(0)
	await expect(panel.getByRole('button', { name: 'Undo', exact: true })).toHaveCount(0)
	await zing.close()
})

test('the VPN guide and the table of what beats what are on the page', async () => {
	const zing = await launchZing()
	const { panel } = await openNetwork(zing)
	await expect(panel.getByTestId('vpn-guide')).toContainText("Zing can't be a VPN")
	await expect(panel.getByRole('group', { name: 'Install WireGuard' })).toContainText('winget install WireGuard.WireGuard')
	const rows = panel.getByTestId('beats-row')
	await expect(rows).toHaveCount(4)
	await expect(rows.first()).toContainText('DNS-level block')
	await expect(rows.first()).toContainText('the script above')
	await zing.close()
})

test('a Windows without encrypted DNS says so before any button and offers no Apply', async () => {
	const zing = await launchZing()
	await hook(zing, { action: 'dns-stub', adapters: JSON.stringify({ adapters: [{ name: 'Ethernet', v4: '', v6enabled: false, v6: null }], doh: [], dohSupported: false }) })
	const { panel } = await openNetwork(zing)
	await expect(panel.getByTestId('dns-unsupported')).toContainText('needs Windows 11')
	await expect(panel.getByRole('button', { name: 'Apply', exact: true })).toHaveCount(0)
	await expect(dnsScript(panel)).toHaveCount(0)
	expect(dnsRan(zing)).toEqual([])
	await zing.close()
})

test('Undo lists what it will set above the script, and the page does not claim more than is known', async () => {
	const zing = await launchZing()
	const { panel } = await openNetwork(zing)
	await panel.getByRole('button', { name: 'Apply', exact: true }).click()
	await expect(panel.getByTestId('dns-notice')).toContainText('Windows reported no errors')
	await expect(panel.getByTestId('dns-notice')).not.toContainText('is on')
	const list = panel.getByTestId('dns-undo-list')
	await expect(list.getByRole('listitem').nth(0)).toHaveText('Ethernet: 10.0.0.1')
	await expect(list.getByRole('listitem').nth(1)).toHaveText('Wi-Fi: automatic (DHCP)')
	await expect(list).toContainText('1.1.1.1: restored to https://cloudflare-dns.com/dns-query')
	await expect(list).toContainText("1.0.0.1: removed from Windows' encrypted DNS list")
	await expect(panel.getByTestId('dns-adapters').locator('..')).toContainText('restores the settings saved when you applied')
	await expect(panel.getByTestId('dns-adapters').locator('..')).toContainText('was set by Zing')
	await expect(panel.getByTestId('dns-adapters').locator('..')).not.toContainText('is on')
	await zing.close()
})

test('a name with hidden characters is spelled out on the page', async () => {
	const adapters = { adapters: [{ name: 'Eth‮ernet', v4: '', v6enabled: false, v6: null }], doh: [], dohSupported: true }
	const zing = await launchZing()
	await hook(zing, { action: 'dns-stub', adapters: JSON.stringify(adapters) })
	const { panel } = await openNetwork(zing)
	await expect(panel.getByTestId('dns-adapters')).toHaveText('Eth\\u202Eernet')
	expect(await dnsScript(panel).inputValue()).toContain('Eth\\u202Eernet')
	expect(await dnsScript(panel).inputValue()).not.toContain('‮')
	await panel.getByRole('button', { name: 'Apply', exact: true }).click()
	await expect(panel.getByTestId('dns-notice')).toContainText('Windows reported no errors')
	// What ran has the real character: the page's spelling is only for reading.
	expect(dnsRan(zing)[0]).toContain('Eth‮ernet')
	await zing.close()
})

test('the VPN guide says a connected VPN uses its own DNS', async () => {
	const zing = await launchZing()
	const { panel } = await openNetwork(zing)
	await expect(panel.getByTestId('vpn-guide')).toContainText("A connected VPN usually uses its own DNS, so the encrypted DNS setting above doesn't apply to traffic that goes through it")
	await zing.close()
})

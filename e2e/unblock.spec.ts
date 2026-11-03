import { createServer, type IncomingMessage, type Server } from 'node:http'
import { createServer as createSocketServer, type AddressInfo } from 'node:net'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, pages, test, type Launched } from './zing'

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
	// Open a Link arrives in a later task; its panel shows the empty state until then.
	await expect(surface.getByRole('dialog', { name: 'Open a Link' })).toBeVisible()
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
	for (const target of ['zing://home', 'file:///c:/windows/win.ini', 'http://127.0.0.1:1/']) {
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

test('a relayed page has no bridge, and what it posts has no effect', async () => {
	const stand = await standIn()
	const zing = await launchRelayed(stand)
	const page = await openRelayed(zing, 'https://site.test/a/page.html')
	const seen = await inPage(page, (g) => {
		const attempt = (f: () => unknown): string => {
			try {
				return `returned ${typeof f()}`
			} catch (e) {
				return `threw ${(e as Error).name}`
			}
		}
		return {
			webview: typeof g.chrome?.webview,
			post: attempt(() => g.chrome!.webview!.postMessage({ kind: 'invoke', id: 1, channel: 'tabs:get', args: [] })),
			hostObject: attempt(() => g.chrome!.webview!.hostObjects!.sync!.zing),
			zing: typeof g.zing,
		}
	})
	console.log('a relayed page sees', JSON.stringify(seen))
	expect(seen.zing).toBe('undefined')
	expect(seen.hostObject).not.toMatch(/returned (object|function)/)
	// The window carries on, and the host recorded no unhandled error (close() fails the test on one).
	expect((await tabsOf(zing)).tabs.length).toBeGreaterThan(0)
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

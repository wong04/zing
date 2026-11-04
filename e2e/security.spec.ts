// X11: link inspection. No test here reaches the internet: three local servers stand in for pages, and
// ZING_E2E_INSPECT_AS (Zing.Host/TestHooks.InspectedAs, honoured only in an e2e run) makes two of them look like flagged
// sites to the check, so the real rules run unchanged.

import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pages, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const NESTED_QUERY = `?q=<script>window.pwned=1</script>"'&to=http://nested.example/`

let clean: Server
let flagged: Server
let other: Server
let cleanBase = ''
let flaggedBase = ''
let otherBase = ''
/** Every request the "flagged" server saw: a warning must come before any of them. */
let flaggedHits: string[] = []
let otherHits: string[] = []

async function listen(handler: (req: IncomingMessage, res: ServerResponse) => void): Promise<{ server: Server; base: string }> {
	const server = createServer(handler)
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	return { server, base: `http://127.0.0.1:${(server.address() as AddressInfo).port}` }
}

function htmlAttribute(text: string): string {
	return text.replace(/&/g, '&amp;').replace(/"/g, '&quot;')
}

function visitAnywayFor(address: string, key = '1'): string {
	return `zing://visit-anyway?to=${encodeURIComponent(address)}&k=${key}`
}

test.beforeAll(async () => {
	const flaggedSite = await listen((req, res) => {
		if (req.url !== '/favicon.ico') flaggedHits.push(req.url ?? '')
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>Flagged site</title><p>flagged ${req.url ?? ''}</p>`)
	})
	flagged = flaggedSite.server
	flaggedBase = flaggedSite.base
	const otherSite = await listen((req, res) => {
		if (req.url !== '/favicon.ico') otherHits.push(req.url ?? '')
		res.setHeader('content-type', 'text/html')
		res.end('<!doctype html><title>Other flagged site</title><p>other</p>')
	})
	other = otherSite.server
	otherBase = otherSite.base
	// The page whose links and attempts the tests use.
	const made = await listen((req, res) => {
		const url = req.url ?? '/'
		if (url === '/redirect') {
			res.statusCode = 302
			res.setHeader('location', `${flaggedBase}/phish`)
			return void res.end()
		}
		if (url === '/redirect-to-visit-anyway') {
			res.statusCode = 302
			res.setHeader('location', visitAnywayFor(`${flaggedBase}/phish`))
			return void res.end()
		}
		res.setHeader('content-type', 'text/html')
		if (url === '/attacks') {
			const target = visitAnywayFor(`${flaggedBase}/phish`)
			return void res.end(`<!doctype html><title>Attacks</title>
				<a id="link" href="${target}">link</a>
				<a id="redirect" href="/redirect-to-visit-anyway">redirect</a>
				<button id="location" onclick="location = '${target}'">location</button>
				<button id="open" onclick="window.open('${target}')">open</button>
				<form method="post" action="${target}"><button id="form">form</button></form>
				<iframe id="frame" src="${target}"></iframe>`)
		}
		if (url === '/frames') return void res.end(`<!doctype html><title>Frames</title><iframe src="${flaggedBase}/frame"></iframe>`)
		if (url === '/popups') return void res.end(`<!doctype html><title>Popups</title><button id="popup" onclick="window.warning = window.open('${flaggedBase}/phish')">open</button>`)
		res.end(`<!doctype html><title>Links</title>
			<a id="flagged" href="${flaggedBase}/phish">flagged</a>
			<a id="flagged2" href="${flaggedBase}/second">flagged again</a>
			<a id="redirected" href="/redirect">redirected</a>
			<a id="nested" href="${htmlAttribute(`${otherBase}/x${NESTED_QUERY}`)}">nested</a>
			<a id="elsewhere" href="${otherBase}/plain">another flagged host</a>`)
	})
	clean = made.server
	cleanBase = made.base
})

test.beforeEach(() => {
	flaggedHits = []
	otherHits = []
})

test.afterAll(async () => {
	for (const server of [clean, flagged, other]) await new Promise<void>((resolve) => server.close(() => resolve()))
})

function standIns(): string {
	return JSON.stringify({
		[flaggedBase.replace('http://', '')]: 'apple.com.secure-login.tk',
		[otherBase.replace('http://', '')]: 'paypal.account-update.xyz',
	})
}

async function launch(): Promise<Launched> {
	return launchZing({ env: { ZING_START_URL: `${cleanBase}/`, ZING_E2E_INSPECT_AS: standIns() } })
}

const tabsOf = (zing: Launched) => zing.chrome.evaluate(() => window.zing.getTabs())

const isWarning = (page: Page): boolean => page.url().startsWith('zing://warning')

async function linksPage(zing: Launched, path = '/'): Promise<Page> {
	return pageWhere(zing.browser, (p) => p.url() === `${cleanBase}${path}`)
}

/** A click at the element's place: Page.click waits on the navigation a refused click starts, which never comes. */
async function press(page: Page, selector: string): Promise<void> {
	const box = await page.locator(selector).boundingBox()
	if (box === null) throw new Error(`${selector} has no box to click`)
	await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2)
}

async function goTo(zing: Launched, address: string): Promise<void> {
	await expect(zing.chrome.locator('[role="tab"]').first()).toBeVisible()
	const bar = zing.chrome.getByRole('textbox', { name: 'Address and search bar' })
	await bar.click()
	await bar.fill(address)
	await bar.press('Enter')
}

test('a flagged link shows a warning; Visit anyway loads it and doesn\'t warn again this session', async () => {
	const zing = await launch()
	await (await linksPage(zing)).locator('#flagged').click()
	const warning = await pageWhere(zing.browser, isWarning)
	await expect(warning.locator('.host')).toHaveText('apple.com.secure-login.tk')
	await expect(warning.getByText('Impersonates Apple')).toBeVisible()
	await expect(warning.getByText('a guess from the address alone')).toBeVisible()
	await expect(warning.getByRole('button', { name: 'Go back' })).toBeVisible()
	// The check ran before anything was requested.
	expect(flaggedHits).toEqual([])

	await warning.getByRole('link', { name: 'Visit anyway' }).click()
	await pageWhere(zing.browser, (p) => p.url() === `${flaggedBase}/phish`)
	expect(flaggedHits).toEqual(['/phish'])

	// Another address on the same host goes straight through, but a different flagged host still warns.
	await goTo(zing, `${cleanBase}/`)
	await (await linksPage(zing)).locator('#flagged2').click()
	await pageWhere(zing.browser, (p) => p.url() === `${flaggedBase}/second`)
	await goTo(zing, `${cleanBase}/`)
	await (await linksPage(zing)).locator('#elsewhere').click()
	await pageWhere(zing.browser, isWarning)
	expect(otherHits).toEqual([])
	await zing.close()
})

test('securityRealtime off shows no warning', async () => {
	const zing = await launch()
	await zing.chrome.evaluate(() => window.zing.setSetting('securityRealtime', false))
	await (await linksPage(zing)).locator('#flagged').click()
	await pageWhere(zing.browser, (p) => p.url() === `${flaggedBase}/phish`)
	expect(pages(zing.browser).some(isWarning)).toBe(false)

	// Turned back on, with no restart, the next one warns.
	await zing.chrome.evaluate(() => window.zing.setSetting('securityRealtime', true))
	await goTo(zing, `${cleanBase}/`)
	await (await linksPage(zing)).locator('#flagged2').click()
	await pageWhere(zing.browser, isWarning)
	expect(flaggedHits).toEqual(['/phish'])
	await zing.close()
})

test('a redirect to a flagged address is warned about before it is requested', async () => {
	const zing = await launch()
	await (await linksPage(zing)).locator('#redirected').click()
	await pageWhere(zing.browser, isWarning)
	expect(flaggedHits).toEqual([])
	await zing.close()
})

const attempts = [
	['a web page linking to zing://visit-anyway is refused', 'link'],
	['a redirect from a web page to zing://visit-anyway is refused', 'redirect'],
	['a web page setting location to zing://visit-anyway is refused', 'location'],
	['a form post to zing://visit-anyway is refused', 'form'],
	['window.open of zing://visit-anyway is refused', 'open'],
] as const

for (const [name, id] of attempts) {
	test(name, async () => {
		const zing = await launch()
		await goTo(zing, `${cleanBase}/attacks`)
		const attacks = await linksPage(zing, '/attacks')
		await press(attacks, `#${id}`)
		await zing.chrome.waitForTimeout(800)
		// The tab is where it was, nothing was requested, and no tab shows the flagged site.
		const { tabs, activeId } = await tabsOf(zing)
		// window.open leaves an empty tab on the refused address; every other attempt leaves the page alone.
		if (id === 'open') expect(tabs.map((t) => t.url.split('?')[0])).toEqual([`${cleanBase}/attacks`, 'zing://visit-anyway/'])
		else expect(tabs.find((t) => t.id === activeId)?.url).toBe(`${cleanBase}/attacks`)
		expect(tabs.some((t) => t.url.startsWith(flaggedBase))).toBe(false)
		expect(flaggedHits).toEqual([])
		console.log(`${id}: tabs afterwards ${JSON.stringify(tabs.map((t) => t.url))}`)

		// Nothing was added to the allowed hosts: the flagged address still warns (from the first tab, whose page the test can reach).
		await zing.chrome.evaluate((first) => window.zing.activateTab(first), tabs[0]!.id)
		await goTo(zing, `${cleanBase}/`)
		await press(await linksPage(zing), '#flagged')
		await pageWhere(zing.browser, isWarning)
		expect(flaggedHits).toEqual([])
		await zing.close()
	})
}

test('an iframe onto zing://visit-anyway gets nothing, and allows nothing', async () => {
	const zing = await launch()
	await goTo(zing, `${cleanBase}/attacks`)
	const attacks = await linksPage(zing, '/attacks')
	await expect(attacks.locator('#frame')).toBeAttached()
	expect(attacks.url()).toBe(`${cleanBase}/attacks`)
	expect(flaggedHits).toEqual([])
	await goTo(zing, `${cleanBase}/`)
	await press(await linksPage(zing), '#flagged')
	await pageWhere(zing.browser, isWarning)
	expect(flaggedHits).toEqual([])
	await zing.close()
})

test('a page that opened the warning\x27s window cannot click Visit anyway for the person', async () => {
	const zing = await launch()
	await goTo(zing, `${cleanBase}/popups`)
	const opener = await linksPage(zing, '/popups')
	await opener.locator('#popup').click()
	const warningTab = async () => (await tabsOf(zing)).tabs.find((t) => t.url.startsWith('zing://warning'))
	await expect.poll(warningTab).toBeDefined()
	// The opener holds the window the warning is in, and can set its location; the warning's own key is not on its side.
	await opener.evaluate((to) => {
		;(globalThis as unknown as { warning: { location: string } }).warning.location = `zing://visit-anyway?to=${encodeURIComponent(to)}&k=00000000000000000000000000000000`
	}, `${flaggedBase}/phish`)
	await opener.waitForTimeout(800)
	expect(await warningTab()).toBeDefined()
	expect(flaggedHits).toEqual([])
	await zing.close()
})

test('the address on the warning is text, and Visit anyway loads it whole, nested to= included', async () => {
	const zing = await launch()
	await (await linksPage(zing)).locator('#nested').click()
	const warning = await pageWhere(zing.browser, isWarning)
	expect(await warning.evaluate(() => (globalThis as unknown as { pwned?: number }).pwned)).toBeUndefined()
	await expect(warning.locator('.address')).toContainText('&to=http://nested.example/')
	expect(otherHits).toEqual([])
	await warning.getByRole('link', { name: 'Visit anyway' }).click()
	await pageWhere(zing.browser, (p) => p.url().startsWith(`${otherBase}/x`))
	expect(otherHits).toHaveLength(1)
	expect(otherHits[0]).toMatch(/^\/x\?q=.*&to=http:\/\/nested\.example\/$/)
	await zing.close()
})

test('Go back from a warning returns to the page the link was on, and does not loop', async () => {
	const zing = await launch()
	await (await linksPage(zing)).locator('#flagged').click()
	const warning = await pageWhere(zing.browser, isWarning)
	await warning.getByRole('button', { name: 'Go back' }).click()
	await pageWhere(zing.browser, (p) => p.url() === `${cleanBase}/`)
	expect(pages(zing.browser).some(isWarning)).toBe(false)
	await zing.close()
})

test('a warning in a tab with no history sends Go back to the start page', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${flaggedBase}/phish`, ZING_E2E_INSPECT_AS: standIns() } })
	const warning = await pageWhere(zing.browser, isWarning)
	await warning.getByRole('button', { name: 'Go back' }).click()
	await pageWhere(zing.browser, (p) => p.url().startsWith('zing://home'))
	expect(flaggedHits).toEqual([])
	await zing.close()
})

test('an iframe onto a flagged address is not warned about', async () => {
	const zing = await launch()
	await goTo(zing, `${cleanBase}/frames`)
	await expect.poll(() => flaggedHits).toEqual(['/frame'])
	expect(pages(zing.browser).some(isWarning)).toBe(false)
	await zing.close()
})

// ---- Open a Link and relayed pages: a local server stands in for every site and for the relay (ZING_E2E_RELAY_URL).

const FLAGGED_SITE = 'https://apple.com.secure-login.tk/verify'

interface RelayStand {
	readonly origin: string
	/** The addresses the host's engine fetched on a relayed page's behalf. */
	readonly fetched: string[]
	close(): Promise<void>
}

async function relayStand(answer: (target: URL) => { status: number; body: string }): Promise<RelayStand> {
	const fetched: string[] = []
	const server = createServer((request, response) => {
		const target = request.headers['x-relay-target']
		if (typeof target !== 'string') return void response.end()
		fetched.push(target)
		const { status, body } = answer(new URL(target))
		response.statusCode = status
		response.setHeader('content-type', 'text/html')
		response.end(body)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	const origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
	return { origin, fetched, close: () => new Promise((resolve) => server.close(() => resolve())) }
}

async function openRelayed(zing: Launched, address: string): Promise<void> {
	await zing.chrome.evaluate(
		(url) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'open-relayed', url }),
		address,
	)
}

test('a flagged address opened through Open a Link is warned about before the relay fetches it, and Visit anyway goes back through the relay', async () => {
	const stand = await relayStand(() => ({ status: 200, body: '<!doctype html><title>Relayed flagged</title><p id="t">relayed ok</p>' }))
	const zing = await launchZing({ env: { ZING_E2E_RELAY_URL: stand.origin } })
	await openRelayed(zing, FLAGGED_SITE)
	const warning = await pageWhere(zing.browser, isWarning)
	await expect(warning.getByText('Open a Link')).toBeVisible()
	expect(stand.fetched).toEqual([])
	await warning.getByRole('link', { name: 'Visit anyway' }).click()
	const relayed = await pageWhere(zing.browser, (p) => p.url().startsWith('zing-relay://apple.com.secure-login.tk'))
	await expect(relayed.locator('#t')).toHaveText('relayed ok')
	expect(stand.fetched).toEqual([FLAGGED_SITE])
	await zing.close()
	await stand.close()
})

test('Visit anyway does not give the relay the session’s OK to ask a third party', async () => {
	const stand = await relayStand(() => ({ status: 404, body: 'no such page' }))
	const zing = await launchZing({ env: { ZING_E2E_RELAY_URL: stand.origin } })
	await openRelayed(zing, FLAGGED_SITE)
	await (await pageWhere(zing.browser, isWarning)).getByRole('link', { name: 'Visit anyway' }).click()
	const relayed = await pageWhere(zing.browser, (p) => p.url().startsWith('zing-relay://apple.com.secure-login.tk'))
	await expect.poll(() => relayed.evaluate('document.body ? document.body.innerText : ""').then(String, () => '')).toContain('Needs your OK')
	expect([...new Set(stand.fetched.map((target) => new URL(target).host))]).toEqual(['apple.com.secure-login.tk'])
	await zing.close()
	await stand.close()
})

test('a relayed page linking to a flagged address is warned about, and the relay never fetches it', async () => {
	const stand = await relayStand((target) => ({
		status: 200,
		body: target.host === 'site.test' ? `<!doctype html><title>Site</title><a id="go" href="${FLAGGED_SITE}">go</a>` : '<!doctype html><title>Flagged</title>x',
	}))
	const zing = await launchZing({ env: { ZING_E2E_RELAY_URL: stand.origin } })
	await openRelayed(zing, 'https://site.test/')
	const site = await pageWhere(zing.browser, (p) => p.url().startsWith('zing-relay://site.test'))
	await press(site, '#go')
	// The tab is on the warning (read from the tab list: the page left the relay scheme and the test's handle on it goes stale).
	await expect.poll(async () => (await tabsOf(zing)).tabs.some((t) => t.url.startsWith('zing://warning'))).toBe(true)
	await zing.chrome.waitForTimeout(800)
	expect(stand.fetched.map((target) => new URL(target).host)).toEqual(['site.test'])
	await zing.close()
	await stand.close()
})

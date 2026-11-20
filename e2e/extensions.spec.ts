// Settings' Extensions (Load unpacked). Extensions run only in the persistent profile: in the in-memory one the engine
// installs them but their content scripts and request rules do nothing. So with Stay signed in off the pane explains and
// offers it, and an extension loaded runs from the next launch with both settings on. The fixture extension marks every
// page, blocks blocked.example and has an action popup.
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { existsSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi; chrome: { webview: { postMessage(m: unknown): void } } }

const FIXTURE = join(import.meta.dirname, 'fixtures', 'extension')
const GIF = Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64')
const MAP_HOSTS = { ZING_E2E_MAP_HOSTS: 'MAP blocked.example 127.0.0.1' }

let server: Server
let port = 0

/** Titles itself with whether the extension's content script marked the page, and whether its rule refused blocked.example. */
const page = `<!doctype html><title>waiting</title>
<script>
	const img = new Image()
	img.onload = () => done('loaded')
	img.onerror = () => done('refused')
	img.src = 'http://blocked.example:' + location.port + '/pixel?' + Date.now()
	function done(pixel) {
		document.title = (document.documentElement.getAttribute('data-zing-test-extension') ?? 'none') + '/' + pixel
	}
</script>`

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('cache-control', 'no-store')
		if (req.url?.startsWith('/pixel')) {
			res.setHeader('content-type', 'image/gif')
			res.end(GIF)
			return
		}
		res.setHeader('content-type', 'text/html')
		res.end(page)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	port = (server.address() as AddressInfo).port
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

async function openSheet(zing: Launched): Promise<Page> {
	await zing.chrome.evaluate(() => window.zing.openSettings('extensions'))
	return pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
}


/** The engine's pages at an extension's own popup.html. Playwright lists no chrome-extension:// page, so the targets are read over CDP. */
async function extensionPages(zing: Launched): Promise<{ type: string; title: string }[]> {
	const cdp = await zing.browser.newBrowserCDPSession()
	const { targetInfos } = (await cdp.send('Target.getTargets')) as { targetInfos: { type: string; url: string; title: string }[] }
	await cdp.detach()
	return targetInfos.filter((t) => t.url.startsWith('chrome-extension://') && t.url.endsWith('/popup.html')).map((t) => ({ type: t.type, title: t.title }))
}

/** Loads the test page in the active tab and answers its title once that page, not the one before, has set it. */
async function load(zing: Launched): Promise<string> {
	const id = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId!
	const url = `http://127.0.0.1:${port}/page?${Date.now()}`
	await zing.chrome.evaluate(([tab, to]) => window.zing.navigateTab(tab!, to!), [id, url])
	const tab = async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === id)
	await expect.poll(async () => {
		const now = await tab()
		return now?.url === url && /^(ran|none)\//.test(now.title)
	}).toBe(true)
	return (await tab())!.title
}

test('with Stay signed in off, Extensions says extensions need it and turns it on', async () => {
	const zing = await launchZing()
	const sheet = await openSheet(zing)

	await expect(sheet.getByText('Extensions need Stay signed in to sites', { exact: true })).toBeVisible()
	await expect(sheet.getByRole('switch', { name: 'Allow extensions' })).toHaveCount(0)
	await expect(sheet.getByRole('button', { name: 'Choose Folder' })).toBeDisabled()
	await sheet.getByRole('button', { name: 'Turn On' }).click()

	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getSettings())).persistentProfile).toBe(true)
	await expect(sheet.getByText('Extensions need Stay signed in to sites', { exact: true })).toHaveCount(0)
	await expect(sheet.getByRole('switch', { name: 'Allow extensions' })).toBeVisible()
	await zing.close()
})

test('a loaded extension runs in tabs from the next launch, opens its popup, and stops when turned off or removed', async () => {
	test.setTimeout(180_000)
	const first = await launchZing({ env: MAP_HOSTS })
	await first.chrome.evaluate(() => window.zing.setSetting('restoreTabs', false))
	await first.chrome.evaluate(() => window.zing.setSetting('blockTrackers', false))
	await first.chrome.evaluate(() => window.zing.setSetting('persistentProfile', true))
	await first.chrome.evaluate(() => window.zing.setSetting('extensions', true))
	// Not running yet: both settings apply at launch.
	expect(await first.chrome.evaluate(() => window.zing.getExtensions())).toEqual({ running: false, items: [] })
	const sheet = await openSheet(first)
	await first.chrome.evaluate((folder) => window.chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: [folder] }), FIXTURE)
	await sheet.getByRole('button', { name: 'Choose Folder' }).click()
	await expect(sheet.getByText('Zing Test Extension 1.0')).toBeVisible()
	await expect(sheet.getByText('Not running. Relaunch Zing to run extensions.')).toBeVisible()
	// Zing's own copy, so the folder it came from can move.
	const copies = join(first.userDataDir, 'Extensions')
	expect(readdirSync(copies).filter((n) => n.startsWith('zing-test-extension-'))).toHaveLength(1)
	expect(await load(first)).toBe('none/loaded')
	await first.close()

	const second = await launchZing({ env: MAP_HOSTS, userDataDir: first.userDataDir })
	// The first page waits for its extensions, so even it is marked.
	expect(await load(second)).toBe('ran/refused')
	const state = await second.chrome.evaluate(() => window.zing.getExtensions())
	expect(state.running).toBe(true)
	const key = state.items[0]!.key

	// The toolbar's button lists it; its popup opens in a view of its own, in the profile it runs in.
	await second.chrome.getByRole('button', { name: 'Extensions' }).click()
	await expect.poll(() => second.menus().at(-1)).toEqual(['Zing Test Extension', null, 'Manage Extensions'])
	await second.chrome.evaluate((k) => window.zing.openExtensionPopup(k), key)
	await expect.poll(() => extensionPages(second)).toEqual([{ type: 'page', title: 'Zing Test Extension popup' }])

	// The engine applies a change a moment after its call returns, so a page loaded straight after may still have the old state.
	await second.chrome.evaluate((k) => window.zing.setExtensionEnabled(k, false), key)
	await expect.poll(() => load(second), { timeout: 30_000 }).toBe('none/loaded')
	await second.chrome.evaluate((k) => window.zing.setExtensionEnabled(k, true), key)
	await expect.poll(() => load(second), { timeout: 30_000 }).toBe('ran/refused')

	expect(await second.chrome.evaluate((k) => window.zing.removeExtension(k), key)).toEqual({ running: true, items: [] })
	await expect.poll(() => load(second), { timeout: 30_000 }).toBe('none/loaded')
	expect(readdirSync(copies).filter((n) => n.startsWith('zing-test-extension-'))).toHaveLength(0)
	await expect.poll(() => extensionPages(second)).toEqual([])
	await second.close()
	expect(existsSync(FIXTURE)).toBe(true)
})

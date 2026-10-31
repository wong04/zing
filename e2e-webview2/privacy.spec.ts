import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, test, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

const SET = `<script>
	localStorage.setItem('k', 'v')
	document.cookie = 'k=v; path=/'
	document.title = 'set:' + localStorage.getItem('k') + ':' + document.cookie
</script>`
const READ = `<script>document.title = JSON.stringify({ ls: localStorage.getItem('k'), cookie: document.cookie })</script>`

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>…</title>${req.url?.startsWith('/set') ? SET : READ}`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

async function openIn(chrome: Page, id: string, path: string): Promise<void> {
	await chrome.evaluate(([tab, url]) => window.zing.navigateTab(tab!, url!), [id, `${base}${path}`])
}

async function newTab(chrome: Page): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	return (await chrome.evaluate(() => window.zing.getTabs())).activeId!
}

const titleOf = async (chrome: Page, id: string) => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === id)?.title

test('a site keeps nothing once its last tab closes', async () => {
	const zing = await launchZing()
	const first = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId!
	await openIn(zing.chrome, first, '/set')
	await expect.poll(() => titleOf(zing.chrome, first)).toBe('set:v:k=v')

	// A second tab on the same site keeps its data alive when the first one closes.
	const second = await newTab(zing.chrome)
	await openIn(zing.chrome, second, '/read')
	await expect.poll(() => titleOf(zing.chrome, second)).toBe(JSON.stringify({ ls: 'v', cookie: 'k=v' }))
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), first)

	const third = await newTab(zing.chrome)
	await openIn(zing.chrome, third, '/read?still')
	await expect.poll(() => titleOf(zing.chrome, third)).toBe(JSON.stringify({ ls: 'v', cookie: 'k=v' }))

	// Now close every tab on the site: the start page is left, and the site is purged.
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), second)
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), third)
	const again = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId!
	await pageWhere(zing.browser, (p) => p.url().startsWith('zing://home'))
	await expect
		.poll(async () => {
			await openIn(zing.chrome, again, `/read?${Date.now()}`)
			await new Promise((resolve) => setTimeout(resolve, 300))
			return titleOf(zing.chrome, again)
		}, { timeout: 10_000 })
		.toBe(JSON.stringify({ ls: null, cookie: '' }))
	await zing.close()
})

test('the browsing profile is in memory unless persistentProfile is on, which keeps sign-ins across a relaunch', async () => {
	const keptAfterRelaunch = async (persistent: boolean): Promise<string | null> => {
		const first = await launchZing()
		await first.chrome.evaluate((on) => window.zing.setSetting('persistentProfile', on), persistent)
		await first.close()
		// The profile is chosen at launch.
		const second = await launchZing({ userDataDir: first.userDataDir })
		const tab = (await second.chrome.evaluate(() => window.zing.getTabs())).activeId!
		await openIn(second.chrome, tab, '/set')
		await expect.poll(() => titleOf(second.chrome, tab)).toBe('set:v:k=v')
		await second.close()
		const third = await launchZing({ userDataDir: first.userDataDir })
		const again = (await third.chrome.evaluate(() => window.zing.getTabs())).activeId!
		await openIn(third.chrome, again, '/read')
		await expect.poll(async () => (await titleOf(third.chrome, again))?.startsWith('{')).toBe(true)
		// localStorage, not the cookie: Chromium drops session cookies on restart even in a kept profile.
		const seen = JSON.parse((await titleOf(third.chrome, again))!) as { ls: string | null }
		await third.close()
		return seen.ls
	}
	expect(await keptAfterRelaunch(false)).toBeNull()
	expect(await keptAfterRelaunch(true)).toBe('v')
})

test('the Back button menu reads the page history without errors', async () => {
	const zing = await launchZing()
	const tab = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId!
	await openIn(zing.chrome, tab, '/read?one')
	await expect.poll(() => titleOf(zing.chrome, tab)).toContain('"ls"')
	await openIn(zing.chrome, tab, '/read?two')
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs[0]?.canGoBack).toBe(true)
	// Opens a native menu; close() fails the test if the host recorded any error reading the history.
	await zing.chrome.evaluate(() => window.zing.openHistoryMenu('back', { x: 20, y: 60 }))
	await zing.close()
})

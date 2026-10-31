// W2: the core plumbing on WebView2 — tabs, settings, library and session on disk, layout, notices, popups, windows.
import { existsSync, readFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, test, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pages, pageWhere } from './zing'

// Evaluated in the chrome renderer, where api.ts puts the bridge on window.
declare const window: { readonly zing: ZingApi }

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		const name = (req.url ?? '/').slice(1) || 'home'
		if (name === 'opener') return res.end(`<!doctype html><title>Opener</title><button onclick="window.open('/popup')">open</button>`)
		if (name === 'popup') return res.end('<!doctype html><title>Popup</title><script>document.title = window.opener ? "Popup with opener" : "Popup alone"</script>')
		res.end(`<!doctype html><title>Page ${name}</title><p>${name}</p>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const tabsOf = (chrome: Page) => chrome.evaluate(() => window.zing.getTabs())
const run = (chrome: Page, id: string) => chrome.evaluate((command) => window.zing.runCommand(command as never), id)

async function activeId(chrome: Page): Promise<string> {
	const { activeId } = await tabsOf(chrome)
	if (!activeId) throw new Error('no active tab')
	return activeId
}

async function visit(chrome: Page, id: string, path: string): Promise<void> {
	await chrome.evaluate(([tab, url]) => window.zing.navigateTab(tab!, url!), [id, `${base}/${path}`])
	await expect.poll(async () => (await tabsOf(chrome)).tabs.find((t) => t.id === id)?.title).toBe(`Page ${path}`)
}

test('New Tab adds a tab, closing it goes back, and closing the last leaves a fresh one', async () => {
	const zing = await launchZing()
	const first = await activeId(zing.chrome)
	await visit(zing.chrome, first, 'one')
	await run(zing.chrome, 'tab.new')
	await expect(zing.chrome.locator('[role="tab"]')).toHaveCount(2)
	const second = await activeId(zing.chrome)
	expect(second).not.toBe(first)
	await visit(zing.chrome, second, 'two')

	await zing.chrome.evaluate((id) => window.zing.activateTab(id), first)
	await expect.poll(() => activeId(zing.chrome)).toBe(first)
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), first)
	await expect(zing.chrome.locator('[role="tab"]')).toHaveCount(1)
	expect(await activeId(zing.chrome)).toBe(second)

	await zing.chrome.evaluate((id) => window.zing.closeTab(id), second)
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.length).toBe(1)
	expect(await activeId(zing.chrome)).not.toBe(second)
	await zing.close()
})

test('pinning moves a tab in front of the others', async () => {
	const zing = await launchZing()
	const first = await activeId(zing.chrome)
	await run(zing.chrome, 'tab.new')
	const second = await activeId(zing.chrome)
	await zing.chrome.evaluate((id) => window.zing.pinTab(id), second)
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.map((t) => [t.id, t.pinned])).toEqual([
		[second, true],
		[first, false],
	])
	await zing.chrome.evaluate((id) => window.zing.unpinTab(id), second)
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.every((t) => !t.pinned)).toBe(true)
	await zing.close()
})

test('a changed setting is written to the profile and survives a relaunch', async () => {
	const first = await launchZing()
	await first.chrome.evaluate(() => window.zing.setSetting('searchEngine', 'duckduckgo'))
	await expect.poll(() => first.chrome.evaluate(async () => (await window.zing.getSettings()).searchEngine)).toBe('duckduckgo')
	await first.close()
	expect(JSON.parse(readFileSync(join(first.userDataDir, 'settings.json'), 'utf8')).searchEngine).toBe('duckduckgo')

	const second = await launchZing({ userDataDir: first.userDataDir })
	expect(await second.chrome.evaluate(async () => (await window.zing.getSettings()).searchEngine)).toBe('duckduckgo')
	await second.close()
})

test('an invalid setting is refused with the host error', async () => {
	const zing = await launchZing()
	const error = await zing.chrome.evaluate(() => window.zing.setSetting('searchEngine', 'altavista' as never).then(() => 'accepted', (e: Error) => e.name))
	expect(error).toBe('InvalidSettingException')
	await zing.close()
})

test('a favourite is on the Favourites bar and survives a relaunch', async () => {
	const first = await launchZing()
	await visit(first.chrome, await activeId(first.chrome), 'saved')
	await run(first.chrome, 'library.addFavourite')
	await expect(first.chrome.locator('.favourites-bar .fav-title')).toHaveText('Page saved')
	await first.close()

	const second = await launchZing({ userDataDir: first.userDataDir })
	await expect(second.chrome.locator('.favourites-bar .fav-title')).toHaveText('Page saved')
	await second.close()
})

test('restoreTabs brings the tabs back after a relaunch', async () => {
	const first = await launchZing()
	await first.chrome.evaluate(() => window.zing.setSetting('restoreTabs', true))
	await visit(first.chrome, await activeId(first.chrome), 'one')
	await run(first.chrome, 'tab.new')
	await visit(first.chrome, await activeId(first.chrome), 'two')
	await expect.poll(() => existsSync(join(first.userDataDir, 'session.json'))).toBe(true)
	await first.close()

	const second = await launchZing({ userDataDir: first.userDataDir })
	await expect.poll(async () => (await tabsOf(second.chrome)).tabs.map((t) => t.url)).toEqual([`${base}/one`, `${base}/two`])
	await pageWhere(second.browser, (p) => p.url() === `${base}/two`)
	await second.close()
})

test('pinning the sidebar narrows the page from the left', async () => {
	const zing = await launchZing()
	await visit(zing.chrome, await activeId(zing.chrome), 'one')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/one`)
	const width = () => page.evaluate(() => (globalThis as unknown as { innerWidth: number }).innerWidth)
	const pinned = await zing.chrome.evaluate(async () => (await window.zing.getSettings()).sidebarPinned)
	const before = await width()
	await zing.chrome.evaluate((on) => window.zing.setSetting('sidebarPinned', on), !pinned)
	await expect.poll(width).not.toBe(before)
	await zing.close()
})

test('a command that is not ported yet shows a notice', async () => {
	const zing = await launchZing()
	await run(zing.chrome, 'page.reader')
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(overlay.getByText(/isn't implemented yet/)).toBeVisible()
	await zing.close()
})

test('a popup opens as a tab and keeps window.opener', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/opener` } })
	const opener = await pageWhere(zing.browser, (p) => p.url() === `${base}/opener`)
	await opener.getByRole('button', { name: 'open' }).click()
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.map((t) => t.title)).toContain('Popup with opener')
	await zing.close()
})

test('New Window opens a second chrome', async () => {
	const zing = await launchZing()
	await run(zing.chrome, 'window.new')
	await expect.poll(() => pages(zing.browser).filter((p) => p.url().endsWith('#chrome')).length).toBe(2)
	await zing.close()
})

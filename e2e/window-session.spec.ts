// Every window in the session, the update relaunch that brings them all back once, and closing a window: the confirm and Ctrl+Shift+T.
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Browser, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { CHROME_URL, launchZing, pages, pageWhere, scratchDir, test } from './zing'

declare const window: { readonly zing: ZingApi }

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		const name = (req.url ?? '/').slice(1) || 'home'
		res.end(`<!doctype html><title>Page ${name}</title><p>${name}</p>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const post = (chrome: Page, message: Record<string, unknown>): Promise<void> =>
	chrome.evaluate((m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage(m), message)
const tabsOf = (chrome: Page) => chrome.evaluate(() => window.zing.getTabs())
const urlsOf = async (chrome: Page) => (await tabsOf(chrome)).tabs.map((t) => t.url)
const run = (chrome: Page, id: string) => chrome.evaluate((command) => window.zing.runCommand(command as never), id)
const chromes = (zing: { browser: Browser }) => pages(zing.browser).filter((p) => p.url() === CHROME_URL)

async function visit(chrome: Page, path: string): Promise<void> {
	const id = (await tabsOf(chrome)).activeId!
	await chrome.evaluate(([tab, url]) => window.zing.navigateTab(tab!, url!), [id, `${base}/${path}`])
	await expect.poll(async () => (await tabsOf(chrome)).tabs.find((t) => t.id === id)?.title).toBe(`Page ${path}`)
}

/** Opens a second window and hands back its chrome, once its bridge is up. */
async function secondWindow(zing: Awaited<ReturnType<typeof launchZing>>): Promise<Page> {
	const before = chromes(zing)
	await run(zing.chrome, 'window.new')
	await expect.poll(() => chromes(zing).length).toBe(before.length + 1)
	const other = chromes(zing).find((p) => !before.includes(p))!
	await other.waitForLoadState('domcontentloaded')
	await other.waitForFunction(() => typeof window.zing?.getTabs === 'function')
	return other
}

/** The windows' tabs in no particular order, sorted so two runs compare. */
async function windowsOf(zing: Awaited<ReturnType<typeof launchZing>>): Promise<string[][]> {
	const all = await Promise.all(chromes(zing).map(urlsOf))
	return all.map((urls) => urls.filter((u) => u.startsWith('http'))).sort((a, b) => (a[0] ?? '').localeCompare(b[0] ?? ''))
}

test('with Reopen tabs on, every window is saved, and a saved session of two windows comes back as two', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate(() => window.zing.setSetting('restoreTabs', true))
	await visit(zing.chrome, 'one')
	const other = await secondWindow(zing)
	await visit(other, 'two')
	const session = join(zing.userDataDir, 'session.json')
	await expect
		.poll(() => (existsSync(session) ? (JSON.parse(readFileSync(session, 'utf8')) as { version: number; windows: { tabs: { url: string }[] }[] }) : null))
		.toMatchObject({ version: 2, windows: [{ tabs: [{ url: `${base}/one` }] }, { tabs: [{ url: `${base}/two` }] }] })
	await zing.close()

	const userDataDir = scratchDir('zing-host-data-')
	writeFileSync(join(userDataDir, 'settings.json'), JSON.stringify({ restoreTabs: true }))
	const tab = (path: string) => ({ url: `${base}/${path}`, title: `Page ${path}`, pinned: false, active: true })
	writeFileSync(join(userDataDir, 'session.json'), JSON.stringify({ version: 2, windows: [{ tabs: [tab('one')] }, { tabs: [tab('two')] }] }))
	const again = await launchZing({ userDataDir })
	await expect.poll(() => windowsOf(again)).toEqual([[`${base}/one`], [`${base}/two`]])
	await again.close()
})

test('Relaunch to update brings every window back once, with Reopen tabs off', async () => {
	test.setTimeout(180_000)
	const zing = await launchZing()
	await visit(zing.chrome, 'one')
	const other = await secondWindow(zing)
	await visit(other, 'two')
	await post(zing.chrome, { kind: 'test', action: 'update-ready', version: '0.9.1', quits: true })
	await zing.chrome.evaluate(() => window.zing.checkForUpdates())
	const pill = await pageWhere(zing.browser, (p) => p.url().endsWith('#update-pill'))
	await expect(pill.getByRole('button', { name: 'Relaunch to update' })).toContainText('Your tabs will reopen')
	await pill.getByRole('button', { name: 'Relaunch to update' }).click()
	await zing.quit()
	const relaunch = join(zing.userDataDir, 'relaunch-session.json')
	expect(existsSync(relaunch)).toBe(true)
	expect(existsSync(join(zing.userDataDir, 'session.json'))).toBe(false)

	const updated = await launchZing({ userDataDir: zing.userDataDir })
	await expect.poll(() => windowsOf(updated)).toEqual([[`${base}/one`], [`${base}/two`]])
	expect(existsSync(relaunch)).toBe(false)
	await updated.quit()

	const later = await launchZing({ userDataDir: zing.userDataDir })
	await expect.poll(() => chromes(later).length).toBe(1)
	expect(await windowsOf(later)).toEqual([[]])
	await later.close()
})

test('closing a window of tabs asks first; Cancel keeps it, and Ctrl+Shift+T in another window brings it back', async () => {
	const zing = await launchZing()
	const other = await secondWindow(zing)
	await visit(other, 'a')
	await run(other, 'tab.new')
	await visit(other, 'b')

	await post(zing.chrome, { kind: 'test', action: 'close-window-answer', close: false })
	await run(other, 'window.close')
	await expect.poll(() => zing.dialogs().filter((d) => d.kind === 'close-window').length).toBe(1)
	expect(zing.dialogs().find((d) => d.kind === 'close-window')?.url).toContain('2 tabs')
	expect(chromes(zing)).toHaveLength(2)

	await post(zing.chrome, { kind: 'test', action: 'close-window-answer', close: true })
	const gone = new Promise<void>((resolve) => other.once('close', () => resolve()))
	void run(other, 'window.close').catch(() => undefined)
	await gone
	expect(chromes(zing)).toHaveLength(1)

	// The first window has no closed tab of its own, so the closed window comes back.
	await run(zing.chrome, 'tab.reopenClosed')
	await expect.poll(() => chromes(zing).length).toBe(2)
	const back = chromes(zing).find((p) => p !== zing.chrome)!
	await back.waitForFunction(() => typeof window.zing?.getTabs === 'function')
	await expect.poll(() => urlsOf(back)).toEqual([`${base}/a`, `${base}/b`])
	expect(await run(zing.chrome, 'tab.reopenClosed')).toMatchObject({ status: 'not-applicable' })
	await zing.close()
})

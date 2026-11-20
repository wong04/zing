// Every window in the session, the update relaunch that brings them all back once, and closing a window: the confirm and Ctrl+Shift+T.
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Browser, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { CHROME_URL, launchZing, pages, pageWhere, scratchDir, test } from './zing'

declare const window: { readonly zing: ZingApi }

type Zing = Awaited<ReturnType<typeof launchZing>>

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
async function secondWindow(zing: Zing): Promise<Page> {
	const before = chromes(zing)
	await run(zing.chrome, 'window.new')
	await expect.poll(() => chromes(zing).length).toBe(before.length + 1)
	const other = chromes(zing).find((p) => !before.includes(p))!
	await other.waitForLoadState('domcontentloaded')
	await other.waitForFunction(() => typeof window.zing?.getTabs === 'function')
	return other
}

/** The windows' tabs in no particular order, sorted so two runs compare. */
async function windowsOf(zing: Zing): Promise<string[][]> {
	const all = await Promise.all(chromes(zing).map(urlsOf))
	return all.map((urls) => urls.filter((u) => u.startsWith('http'))).sort((a, b) => (a[0] ?? '').localeCompare(b[0] ?? ''))
}

/** Two windows, one page each. */
async function twoWindows(restoreTabs: boolean): Promise<Zing> {
	const zing = await launchZing()
	if (restoreTabs) await zing.chrome.evaluate(() => window.zing.setSetting('restoreTabs', true))
	await visit(zing.chrome, 'one')
	const other = await secondWindow(zing)
	await visit(other, 'two')
	return zing
}

type SavedSession = { version: number; windows: { tabs: { url: string }[] }[] }

const sessionOf = (userDataDir: string): SavedSession | null => {
	const session = join(userDataDir, 'session.json')
	return existsSync(session) ? (JSON.parse(readFileSync(session, 'utf8')) as SavedSession) : null
}

/** The host quits by itself (the quit command, the taskbar, an update): the harness only waits for it and checks it went cleanly. */
async function quitsByItself(zing: Zing, quit: () => Promise<unknown>): Promise<void> {
	const gone = new Promise<void>((resolve) => zing.chrome.once('close', () => resolve()))
	void quit().catch(() => undefined)
	await gone
	await zing.quit()
}

test('Quit Zing saves every window, and the next launch brings them all back', async () => {
	const zing = await twoWindows(true)
	await expect.poll(() => sessionOf(zing.userDataDir)).toMatchObject({ version: 2, windows: [{ tabs: [{ url: `${base}/one` }] }, { tabs: [{ url: `${base}/two` }] }] })
	await quitsByItself(zing, () => run(zing.chrome, 'app.quit'))
	// Nothing was asked: Reopen tabs is on, so nothing is lost.
	expect(zing.dialogs().filter((d) => d.kind === 'close-window')).toEqual([])

	const again = await launchZing({ userDataDir: zing.userDataDir })
	await expect.poll(() => windowsOf(again)).toEqual([[`${base}/one`], [`${base}/two`]])
	await again.close()
})

test('the taskbar closing every window at once is a quit: every window is saved and comes back', async () => {
	const zing = await twoWindows(true)
	await expect.poll(() => sessionOf(zing.userDataDir)?.windows.length).toBe(2)
	await quitsByItself(zing, () => post(zing.chrome, { kind: 'test', action: 'close-all-as-taskbar' }))

	const again = await launchZing({ userDataDir: zing.userDataDir })
	await expect.poll(() => windowsOf(again)).toEqual([[`${base}/one`], [`${base}/two`]])
	await again.close()
})

test('Quit Zing with Reopen tabs off asks once for every window, and Cancel keeps them all', async () => {
	const zing = await twoWindows(false)
	await post(zing.chrome, { kind: 'test', action: 'close-window-answer', close: false })
	await run(zing.chrome, 'app.quit')
	await expect.poll(() => zing.dialogs().filter((d) => d.kind === 'close-window').map((d) => d.url)).toEqual([expect.stringContaining('its 2 windows and 2 tabs')])
	expect(chromes(zing)).toHaveLength(2)
	await post(zing.chrome, { kind: 'test', action: 'close-window-answer', close: true })
	await quitsByItself(zing, () => run(zing.chrome, 'app.quit'))
	expect(zing.dialogs().filter((d) => d.kind === 'close-window')).toHaveLength(2)
	expect(existsSync(join(zing.userDataDir, 'session.json'))).toBe(false)
})

test('an old single-window session.json loads, is saved again as version 2, and comes back after a real quit', async () => {
	const userDataDir = scratchDir('zing-host-data-')
	writeFileSync(join(userDataDir, 'settings.json'), JSON.stringify({ restoreTabs: true }))
	writeFileSync(join(userDataDir, 'session.json'), JSON.stringify([{ url: `${base}/old`, title: 'Page old', pinned: false, active: true }]))
	const zing = await launchZing({ userDataDir })
	await expect.poll(() => windowsOf(zing)).toEqual([[`${base}/old`]])
	await visit(await secondWindow(zing), 'new')
	await expect.poll(() => sessionOf(userDataDir)).toMatchObject({ version: 2, windows: [{ tabs: [{ url: `${base}/old` }] }, { tabs: [{ url: `${base}/new` }] }] })
	await quitsByItself(zing, () => run(zing.chrome, 'app.quit'))

	const again = await launchZing({ userDataDir })
	await expect.poll(() => windowsOf(again)).toEqual([[`${base}/new`], [`${base}/old`]])
	await again.close()
})

async function relaunchIntoUpdate(zing: Zing): Promise<void> {
	await post(zing.chrome, { kind: 'test', action: 'update-ready', version: '0.9.1', restart: 'quits' })
	await zing.chrome.evaluate(() => window.zing.checkForUpdates())
	const pill = await pageWhere(zing.browser, (p) => p.url().endsWith('#update-pill'))
	await expect(pill.getByRole('button', { name: 'Relaunch to update' })).toContainText('Your tabs will reopen')
	await quitsByItself(zing, () => pill.getByRole('button', { name: 'Relaunch to update' }).click())
}

test('Relaunch to update brings every window back once, with Reopen tabs off', async () => {
	test.setTimeout(180_000)
	const zing = await twoWindows(false)
	await relaunchIntoUpdate(zing)
	const relaunch = join(zing.userDataDir, 'relaunch-session.json')
	expect(existsSync(relaunch)).toBe(true)
	expect(existsSync(join(zing.userDataDir, 'session.json'))).toBe(false)

	const updated = await launchZing({ userDataDir: zing.userDataDir })
	await expect.poll(() => windowsOf(updated)).toEqual([[`${base}/one`], [`${base}/two`]])
	expect(existsSync(relaunch)).toBe(false)
	expect(existsSync(`${relaunch}.taken`)).toBe(false)
	await updated.quit()

	const later = await launchZing({ userDataDir: zing.userDataDir })
	await expect.poll(() => chromes(later).length).toBe(1)
	expect(await windowsOf(later)).toEqual([[]])
	await later.close()
})

test('Relaunch to update with Reopen tabs on brings every window back once, not twice', async () => {
	test.setTimeout(180_000)
	const zing = await twoWindows(true)
	await expect.poll(() => sessionOf(zing.userDataDir)?.windows.length).toBe(2)
	await relaunchIntoUpdate(zing)
	expect(sessionOf(zing.userDataDir)?.windows).toHaveLength(2)

	const updated = await launchZing({ userDataDir: zing.userDataDir })
	await expect.poll(() => windowsOf(updated)).toEqual([[`${base}/one`], [`${base}/two`]])
	// No more windows come: the session.json saved at the relaunch isn't restored on top.
	await new Promise((resolve) => setTimeout(resolve, 1_000))
	expect(chromes(updated)).toHaveLength(2)
	await updated.close()
})

test('a restart into an update that fails keeps Zing open and deletes the tabs it saved', async () => {
	const zing = await launchZing()
	await visit(zing.chrome, 'one')
	await post(zing.chrome, { kind: 'test', action: 'update-ready', version: '0.9.1', restart: 'fails' })
	await zing.chrome.evaluate(() => window.zing.checkForUpdates())
	const pill = await pageWhere(zing.browser, (p) => p.url().endsWith('#update-pill'))
	await pill.getByRole('button', { name: 'Relaunch to update' }).click()
	// Answered after the click's call on the same view: the relaunch has been handled.
	await pill.evaluate(() => window.zing.getUpdateState())
	expect(existsSync(join(zing.userDataDir, 'relaunch-session.json'))).toBe(false)
	expect(chromes(zing)).toHaveLength(1)
	await zing.close()
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

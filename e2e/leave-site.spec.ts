// A page's beforeunload ("Leave site?") before Zing closes it: a tab, a window, Quit, the taskbar's Close all windows,
// Relaunch to update, and tab sleep. Page dialogs are Zing's own, in the page UI. And the close confirm's "Don't ask again".
import { existsSync, readFileSync } from 'node:fs'
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
		// Asks before it goes, as a page with unsaved work does (Chromium shows it only once the page was used).
		if (name.startsWith('guarded')) return void res.end(`<!doctype html><title>Guarded ${name}</title><input id=f><script>addEventListener('beforeunload', (e) => e.preventDefault())</script>`)
		// Its beforeunload never ends in time: the page has stopped answering.
		if (name === 'stuck') return void res.end(`<!doctype html><title>Stuck</title><input id=f><script>addEventListener('beforeunload', (e) => { const end = Date.now() + 15000; while (Date.now() < end); e.preventDefault() })</script>`)
		res.end(`<!doctype html><title>Page ${name}</title><input id=f><p>${name}</p>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const post = (chrome: Page, message: Record<string, unknown>): Promise<void> =>
	chrome.evaluate((m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage(m), message)
const tabsOf = (chrome: Page) => chrome.evaluate(() => window.zing.getTabs())
const run = (chrome: Page, id: string) => chrome.evaluate((command) => window.zing.runCommand(command as never), id)
const chromes = (zing: { browser: Browser }) => pages(zing.browser).filter((p) => p.url() === CHROME_URL)
const pageUis = (zing: { browser: Browser }) => pages(zing.browser).filter((p) => p.url().endsWith('#pageui'))

/** Opens `path` in a new tab of `chrome` and, with a click into the page, makes it one the person has used. */
async function openUsed(zing: Zing, chrome: Page, path: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const id = (await tabsOf(chrome)).activeId!
	await chrome.evaluate(([tab, url]) => window.zing.navigateTab(tab!, url!), [id, `${base}/${path}`])
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/${path}`)
	await page.waitForLoadState('load')
	// Playwright answers a page's dialogs itself unless a test listens: held, so Zing's own prompt is the one answered.
	page.on('dialog', () => {})
	await page.click('#f')
	return id
}

/** The "Leave site?" prompt, in whichever window's page UI shows it. */
async function leavePrompt(zing: Zing): Promise<Page> {
	let shown: Page | undefined
	await expect
		.poll(async () => {
			for (const ui of pageUis(zing)) if ((await ui.getByRole('alertdialog', { name: 'Leave site?' }).count()) > 0) shown = ui
			return shown !== undefined
		})
		.toBe(true)
	return shown!
}

async function answer(zing: Zing, choice: 'Leave' | 'Stay'): Promise<void> {
	const ui = await leavePrompt(zing)
	await ui.getByRole('alertdialog', { name: 'Leave site?' }).getByRole('button', { name: choice }).click()
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

/** The host quits by itself (the quit command, the taskbar, an update): the harness only waits for it and checks it went cleanly. */
async function quitsByItself(zing: Zing, quit: () => Promise<unknown>): Promise<void> {
	const gone = new Promise<void>((resolve) => zing.chrome.once('close', () => resolve()))
	void quit().catch(() => undefined)
	await gone
	await zing.quit()
}

test('closing a tab with unsaved work asks Leave site? in the page UI: Stay keeps it, Leave closes it', async () => {
	const zing = await launchZing()
	const id = await openUsed(zing, zing.chrome, 'guarded')
	// closeTab resolves once the page has answered: after Stay here.
	const closing = zing.chrome.evaluate((tab) => window.zing.closeTab(tab), id)
	const ui = await leavePrompt(zing)
	await expect(ui.getByRole('alertdialog', { name: 'Leave site?' })).toContainText(base)
	await answer(zing, 'Stay')
	await closing
	await expect(ui.getByRole('alertdialog')).toHaveCount(0)
	expect((await tabsOf(zing.chrome)).tabs.map((t) => t.id)).toContain(id)
	// The page kept everything: it never left its document.
	expect(pages(zing.browser).some((p) => p.url() === `${base}/guarded`)).toBe(true)

	await run(zing.chrome, 'tab.close')
	await answer(zing, 'Leave')
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.map((t) => t.id)).not.toContain(id)
	await zing.close()
})

test('a page that stops answering can not hold its tab open', async () => {
	const zing = await launchZing()
	const id = await openUsed(zing, zing.chrome, 'stuck')
	const started = Date.now()
	await zing.chrome.evaluate((tab) => window.zing.closeTab(tab), id)
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.map((t) => t.id), { timeout: 10_000 }).not.toContain(id)
	expect(Date.now() - started).toBeLessThan(10_000)
	await zing.close()
})

test('closing the window asks the page in a background tab, brought to the front; Stay keeps the window and every tab', async () => {
	const zing = await launchZing()
	const guarded = await openUsed(zing, zing.chrome, 'guarded')
	const plain = await openUsed(zing, zing.chrome, 'plain')
	const before = (await tabsOf(zing.chrome)).tabs.length
	await run(zing.chrome, 'window.close')
	await leavePrompt(zing)
	expect((await tabsOf(zing.chrome)).activeId).toBe(guarded)
	await answer(zing, 'Stay')
	expect(chromes(zing)).toHaveLength(1)
	expect((await tabsOf(zing.chrome)).tabs.map((t) => t.id)).toEqual(expect.arrayContaining([guarded, plain]))
	expect((await tabsOf(zing.chrome)).tabs).toHaveLength(before)

	await quitsByItself(zing, async () => {
		await run(zing.chrome, 'window.close')
		await answer(zing, 'Leave')
	})
})

test('Quit asks the pages of every window, and Stay in one keeps them all', async () => {
	const zing = await launchZing()
	const other = await secondWindow(zing)
	await openUsed(zing, other, 'guarded-two')
	await run(zing.chrome, 'app.quit')
	await answer(zing, 'Stay')
	expect(chromes(zing)).toHaveLength(2)

	await quitsByItself(zing, async () => {
		await run(zing.chrome, 'app.quit')
		await answer(zing, 'Leave')
	})
})

test('the taskbar closing every window asks the pages too', async () => {
	const zing = await launchZing()
	const other = await secondWindow(zing)
	await openUsed(zing, other, 'guarded-two')
	await post(zing.chrome, { kind: 'test', action: 'close-all-as-taskbar' })
	await answer(zing, 'Stay')
	expect(chromes(zing)).toHaveLength(2)

	await quitsByItself(zing, async () => {
		await post(zing.chrome, { kind: 'test', action: 'close-all-as-taskbar' })
		await answer(zing, 'Leave')
	})
})

async function restarts(chrome: Page): Promise<number> {
	const file = join(scratchDir('zing-update-'), 'restarts.json')
	await post(chrome, { kind: 'test', action: 'update-restarts', file })
	await expect.poll(() => existsSync(file)).toBe(true)
	return (JSON.parse(readFileSync(file, 'utf8')) as { restarts: number }).restarts
}

test('Relaunch to update asks the pages first, and Stay keeps Zing running', async () => {
	test.setTimeout(180_000)
	const zing = await launchZing()
	await openUsed(zing, zing.chrome, 'guarded')
	await post(zing.chrome, { kind: 'test', action: 'update-ready', version: '0.9.1', restart: 'quits' })
	await zing.chrome.evaluate(() => window.zing.checkForUpdates())
	const pill = await pageWhere(zing.browser, (p) => p.url().endsWith('#update-pill'))
	const relaunch = pill.getByRole('button', { name: 'Relaunch to update' })
	await relaunch.click()
	await answer(zing, 'Stay')
	expect(await restarts(zing.chrome)).toBe(0)
	expect(chromes(zing)).toHaveLength(1)

	await quitsByItself(zing, async () => {
		await relaunch.click()
		await answer(zing, 'Leave')
	})
})

test('page dialogs are Zing’s own: alert, confirm and prompt answered in the page UI', async () => {
	const zing = await launchZing()
	await openUsed(zing, zing.chrome, 'plain')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/plain`)
	type DialogPage = { confirm(m: string): boolean; prompt(m: string, d: string): string | null; alert(m: string): void; document: { title: string } }

	await page.evaluate(() => void setTimeout(() => ((globalThis as unknown as DialogPage).document.title = String((globalThis as unknown as DialogPage).confirm('Sure?')))))
	// The page UI's view is made the first time it has something to show.
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	const dialog = ui.getByRole('alertdialog', { name: `${base} says` })
	await expect(dialog).toContainText('Sure?')
	await dialog.getByRole('button', { name: 'Cancel' }).click()
	await expect(page).toHaveTitle('false')

	await page.evaluate(() => void setTimeout(() => ((globalThis as unknown as DialogPage).document.title = String((globalThis as unknown as DialogPage).prompt('Name?', 'Ada')))))
	await expect(dialog.getByRole('textbox', { name: 'Answer' })).toHaveValue('Ada')
	await dialog.getByRole('textbox', { name: 'Answer' }).fill('Grace')
	await dialog.getByRole('button', { name: 'OK' }).click()
	await expect(page).toHaveTitle('Grace')

	await page.evaluate(() => void setTimeout(() => {
		;(globalThis as unknown as DialogPage).alert('Saved')
		;(globalThis as unknown as DialogPage).document.title = 'after alert'
	}))
	await expect(dialog).toContainText('Saved')
	await dialog.getByRole('button', { name: 'OK' }).click()
	await expect(page).toHaveTitle('after alert')
	await expect(ui.getByRole('alertdialog')).toHaveCount(0)
	await zing.close()
})

test('a tab whose page would ask Leave site? doesn’t sleep', async () => {
	const zing = await launchZing({ env: { ZING_TEST_CLOCK: '1' } })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const guarded = await openUsed(zing, zing.chrome, 'guarded')
	const idle = await openUsed(zing, zing.chrome, 'plain')
	await openUsed(zing, zing.chrome, 'front')
	await post(zing.chrome, { kind: 'test', action: 'advance-clock', ms: 3 * 60_000 })
	const sleeping = async (id: string) => (await tabsOf(zing.chrome)).tabs.find((t) => t.id === id)!.sleeping
	await expect.poll(() => sleeping(idle)).toBe(true)
	expect(await sleeping(guarded)).toBe(false)
	// Asked without a word: nothing was shown.
	for (const ui of pageUis(zing)) await expect(ui.getByRole('alertdialog')).toHaveCount(0)
	expect(pages(zing.browser).some((p) => p.url() === `${base}/guarded`)).toBe(true)
	// Still awake, so it still asks as the window closes.
	await quitsByItself(zing, async () => {
		await run(zing.chrome, 'window.close')
		await answer(zing, 'Leave')
	})
})

test('Don’t ask again stops the close confirm, and Settings turns it back on', async () => {
	const zing = await launchZing()
	const closeDialogs = () => zing.dialogs().filter((d) => d.kind === 'close-window')
	const other = await secondWindow(zing)
	await openUsed(zing, other, 'one')
	await openUsed(zing, other, 'two')
	await post(zing.chrome, { kind: 'test', action: 'close-window-answer', close: true, dontAskAgain: true })
	await run(other, 'window.close')
	await expect.poll(() => chromes(zing).length).toBe(1)
	expect(closeDialogs()).toHaveLength(1)
	expect(await zing.chrome.evaluate(() => window.zing.getSettings())).toMatchObject({ confirmWindowClose: false })
	await post(zing.chrome, { kind: 'test', action: 'close-window-answer', close: true })

	const third = await secondWindow(zing)
	await openUsed(zing, third, 'three')
	await openUsed(zing, third, 'four')
	await run(third, 'window.close')
	await expect.poll(() => chromes(zing).length).toBe(1)
	expect(closeDialogs()).toHaveLength(1)

	await zing.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await sheet.getByRole('button', { name: 'Tabs' }).click()
	const toggle = sheet.getByRole('switch', { name: 'Ask before closing a window of tabs' })
	await expect(toggle).toHaveAttribute('aria-checked', 'false')
	await toggle.click()
	await expect(toggle).toHaveAttribute('aria-checked', 'true')
	await sheet.getByRole('button', { name: 'Close settings (Esc)' }).click()

	const fourth = await secondWindow(zing)
	await openUsed(zing, fourth, 'five')
	await openUsed(zing, fourth, 'six')
	await run(fourth, 'window.close')
	await expect.poll(() => chromes(zing).length).toBe(1)
	expect(closeDialogs()).toHaveLength(2)
	await zing.close()
})

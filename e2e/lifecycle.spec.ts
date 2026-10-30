import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { expect, test, type ElectronApplication, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { activeTabId, html, navigateTab, newTab, pageAt, serve, tabsOf } from './fixtures'
import { launchZing } from './zing'

const MINUTE = 60_000
const HOUR = 60 * MINUTE
const TEST_CLOCK = { ZING_TEST_CLOCK: '1' }
const NO_SLEEP = { enabled: false, afterMinutes: 30, sleepPinned: false }

type WithZing = { zing: ZingApi }
type PageGlobal = { scrollY: number; scrollTo(x: number, y: number): void }

function advanceClock(app: ElectronApplication, ms: number): Promise<void> {
	return app.evaluate((_, by) => {
		const controls = (globalThis as { __zingTest?: { advanceClock(ms: number): void } }).__zingTest
		if (!controls) throw new Error('ZING_TEST_CLOCK is not set')
		controls.advanceClock(by)
	}, ms)
}

function activate(chrome: Page, id: string): Promise<void> {
	return chrome.evaluate((tab) => (globalThis as unknown as WithZing).zing.activateTab(tab), id)
}

function pin(chrome: Page, id: string): Promise<void> {
	return chrome.evaluate((tab) => (globalThis as unknown as WithZing).zing.pinTab(tab), id)
}

async function tab(chrome: Page, id: string) {
	const found = (await tabsOf(chrome)).tabs.find((t) => t.id === id)
	if (!found) throw new Error(`tab ${id} is gone`)
	return found
}

const pages = {
	'/tall': html('<script>document.title = "tall"</script><div style="height:6000px">tall</div>'),
	'/other': html('<script>document.title = "other"</script>other'),
	'/form': html('<script>document.title = "form"</script><input id="note" type="text">'),
	'/bg': html('<script>document.title = "bg"</script>bg'),
}

test('an idle tab sleeps without a renderer and wakes at its URL and scroll position', async () => {
	const site = await serve(pages)
	const base = `http://127.0.0.1:${site.port}`
	const { app, chrome } = await launchZing({ env: TEST_CLOCK })

	const first = await activeTabId(chrome)
	await navigateTab(chrome, first, `${base}/tall`)
	const tall = await pageAt(app, `${base}/tall`)
	await tall.evaluate(() => (globalThis as unknown as PageGlobal).scrollTo(0, 800))
	await expect.poll(() => tall.evaluate(() => (globalThis as unknown as PageGlobal).scrollY)).toBe(800)

	const second = await newTab(chrome)
	await navigateTab(chrome, second, `${base}/other`)
	await pageAt(app, `${base}/other`)

	await advanceClock(app, 31 * MINUTE)
	await expect.poll(async () => (await tab(chrome, first)).sleeping).toBe(true)
	const asleep = await tab(chrome, first)
	expect(asleep.url).toBe(`${base}/tall`)
	expect(asleep.title).toBe('tall')
	expect((await tab(chrome, second)).sleeping).toBe(false)
	// The strip shows it as asleep.
	await expect(chrome.locator(`[role="tab"][data-tab-id="${first}"]`)).toHaveClass(/\bsleeping\b/)
	// No renderer left behind for the sleeping tab.
	expect(
		await app.evaluate(({ webContents }, url) => webContents.getAllWebContents().some((w) => w.getURL() === url), `${base}/tall`),
	).toBe(false)

	await activate(chrome, first)
	const awake = await tab(chrome, first)
	expect(awake.sleeping).toBe(false)
	expect(awake.url).toBe(`${base}/tall`)
	const woken = await pageAt(app, `${base}/tall`)
	await expect.poll(() => woken.evaluate(() => (globalThis as unknown as PageGlobal).scrollY)).toBe(800)

	await app.close()
	await site.close()
})

test('a tab with unsaved form input stays awake', async () => {
	const site = await serve(pages)
	const base = `http://127.0.0.1:${site.port}`
	const { app, chrome } = await launchZing({ env: TEST_CLOCK })

	const typing = await activeTabId(chrome)
	await navigateTab(chrome, typing, `${base}/form`)
	const form = await pageAt(app, `${base}/form`)
	await form.fill('#note', 'half-written')
	const clean = await newTab(chrome)
	await navigateTab(chrome, clean, `${base}/other`)
	const reading = await newTab(chrome)
	await navigateTab(chrome, reading, `${base}/bg`)
	await pageAt(app, `${base}/bg`)

	await advanceClock(app, 31 * MINUTE)
	await expect.poll(async () => (await tab(chrome, clean)).sleeping).toBe(true)
	expect((await tab(chrome, typing)).sleeping).toBe(false)

	await app.close()
	await site.close()
})

test('auto-close removes idle tabs but spares pinned, active and the last tab', async () => {
	const { app, chrome } = await launchZing({
		env: TEST_CLOCK,
		settings: { autoCloseEnabled: true, autoCloseRule: 'hour', sleepingTabs: NO_SLEEP },
	})

	const idle = await activeTabId(chrome)
	const pinned = await newTab(chrome)
	await pin(chrome, pinned)
	const active = await newTab(chrome)
	await expect.poll(async () => (await tabsOf(chrome)).tabs.length).toBe(3)

	await advanceClock(app, 30 * MINUTE)
	expect((await tabsOf(chrome)).tabs).toHaveLength(3)

	await advanceClock(app, 2 * HOUR)
	await expect.poll(async () => (await tabsOf(chrome)).tabs.map((t) => t.id)).toEqual([pinned, active])
	expect((await tabsOf(chrome)).tabs.some((t) => t.id === idle)).toBe(false)
	expect((await tabsOf(chrome)).activeId).toBe(active)

	await advanceClock(app, 48 * HOUR)
	await pin(chrome, active)
	await advanceClock(app, HOUR)
	expect((await tabsOf(chrome)).tabs).toHaveLength(2)

	await app.close()
})

test('restoreTabs brings tabs back after a relaunch and loads only the active one', async () => {
	const site = await serve(pages)
	const base = `http://127.0.0.1:${site.port}`
	const settings = { restoreTabs: true }
	const first = await launchZing({ settings })

	const bg = await activeTabId(first.chrome)
	await navigateTab(first.chrome, bg, `${base}/bg`)
	await pageAt(first.app, `${base}/bg`)
	await pin(first.chrome, bg)
	const front = await newTab(first.chrome)
	await navigateTab(first.chrome, front, `${base}/other`)
	await pageAt(first.app, `${base}/other`)
	const sessionFile = join(first.userDataDir, 'session.json')
	await expect.poll(() => existsSync(sessionFile)).toBe(true)
	await first.app.close()

	site.hits.length = 0
	const second = await launchZing({ userDataDir: first.userDataDir, settings })
	await expect
		.poll(async () => (await tabsOf(second.chrome)).tabs.map((t) => [t.url, t.pinned]))
		.toEqual([
			[`${base}/bg`, true],
			[`${base}/other`, false],
		])
	const restored = await tabsOf(second.chrome)
	expect(restored.tabs[restored.tabs.findIndex((t) => t.id === restored.activeId)]?.url).toBe(`${base}/other`)
	await pageAt(second.app, `${base}/other`)
	expect(site.hits.some((h) => h.endsWith(' /bg'))).toBe(false)
	expect(site.hits.some((h) => h.endsWith(' /other'))).toBe(true)

	// Waking the lazy tab loads it.
	await activate(second.chrome, restored.tabs[0]!.id)
	await pageAt(second.app, `${base}/bg`)

	await second.app.close()
	await site.close()
})

test('with restoreTabs off nothing is written and a relaunch opens one blank tab', async () => {
	const site = await serve(pages)
	const base = `http://127.0.0.1:${site.port}`
	const first = await launchZing()
	await navigateTab(first.chrome, await activeTabId(first.chrome), `${base}/other`)
	await pageAt(first.app, `${base}/other`)
	await first.app.close()
	expect(existsSync(join(first.userDataDir, 'session.json'))).toBe(false)

	const second = await launchZing({ userDataDir: first.userDataDir })
	const urls = (await tabsOf(second.chrome)).tabs.map((t) => t.url)
	expect(urls).toHaveLength(1)
	expect(urls[0]).not.toContain('127.0.0.1')
	await second.app.close()
	await site.close()
})

test('the browsing profile is in memory unless persistentProfile is on', async () => {
	const site = await serve(pages)
	const url = `http://127.0.0.1:${site.port}/other`
	const storagePath = (app: ElectronApplication) =>
		app.evaluate(({ webContents }, target) => webContents.getAllWebContents().find((w) => w.getURL() === target)?.session.storagePath ?? null, url)

	const off = await launchZing()
	await navigateTab(off.chrome, await activeTabId(off.chrome), url)
	await pageAt(off.app, url)
	expect(await storagePath(off.app)).toBeNull()
	await off.app.close()

	const on = await launchZing({ settings: { persistentProfile: true } })
	await navigateTab(on.chrome, await activeTabId(on.chrome), url)
	await pageAt(on.app, url)
	expect(await storagePath(on.app)).toContain(on.userDataDir)
	await on.app.close()
	await site.close()
})

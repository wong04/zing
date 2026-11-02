// Phase 5 PR A: switching tabs and themes stays quick with 300 tabs and 16 favourites (docs/phase-5.md, Speed).
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { DEFAULT_CUSTOM_COLOR } from '../src/shared/settings'
import { accentThemes, type AccentTheme } from '../src/shared/theme/accent'
import { accentVariables } from '../src/shared/theme/accentVariables'
import { launchZing, test } from './zing'

declare const window: { readonly zing: ZingApi }

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const title = decodeURIComponent((req.url ?? '/').slice(1)) || 'Home'
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>${title}</title><body>${title}</body>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const tabsOf = (chrome: Page) => chrome.evaluate(() => window.zing.getTabs())
const rows = (chrome: Page): Locator => chrome.locator('.rail-tabs [role="tab"]')

async function openPage(chrome: Page, title: string): Promise<void> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await tabsOf(chrome)
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}/${title}`])
	await expect.poll(async () => (await tabsOf(chrome)).tabs.find((t) => t.id === activeId)?.title).toBe(title)
}

async function addFavourite(chrome: Page, title: string): Promise<void> {
	await openPage(chrome, title)
	await chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
}

/** The time from the call to the rail showing the tab as selected, as the vertical-tabs test measures it. */
async function timeTabSwitch(chrome: Page, id: string): Promise<number> {
	return chrome.evaluate(async (tab) => {
		const doc = (globalThis as unknown as { document: { querySelector(s: string): { getAttribute(a: string): string | null } | null } }).document
		const started = performance.now()
		await window.zing.activateTab(tab)
		while (doc.querySelector(`[data-tab-id="${tab}"]`)?.getAttribute('aria-selected') !== 'true') {
			if (performance.now() - started > 5_000) throw new Error(`The rail never showed tab ${tab} as selected`)
			await new Promise((r) => setTimeout(r, 5))
		}
		return performance.now() - started
	}, id)
}

/** From the call to the chrome's --accent showing the new value and a frame having run, in ms. */
async function timeThemeSwitch(chrome: Page, theme: AccentTheme, expected: string): Promise<number> {
	return chrome.evaluate(
		async ([accent, value]) => {
			const g = globalThis as unknown as {
				document: { documentElement: unknown }
				getComputedStyle(element: unknown): { getPropertyValue(name: string): string }
				requestAnimationFrame(callback: () => void): void
			}
			const frame = () => new Promise<void>((resolve) => g.requestAnimationFrame(() => resolve()))
			const started = performance.now()
			await window.zing.setSetting('accent', accent)
			for (let frames = 0; g.getComputedStyle(g.document.documentElement).getPropertyValue('--accent').trim() !== value; frames++) {
				if (frames > 600) throw new Error(`The chrome's --accent never showed ${value} for ${accent}`)
				await frame()
			}
			await frame()
			return performance.now() - started
		},
		[theme, expected] as const,
	)
}

/** The main-thread work an action costs in the chrome view, in ms: DevTools' TaskDuration, as the table in docs/phase-5.md measures it. */
async function workOf(chrome: Page, action: () => Promise<void>): Promise<number> {
	const session = await chrome.context().newCDPSession(chrome)
	await session.send('Performance.enable')
	const taskSeconds = async (): Promise<number> => {
		const { metrics } = await session.send('Performance.getMetrics')
		const task = metrics.find((metric) => metric.name === 'TaskDuration')
		if (!task) throw new Error('DevTools reported no TaskDuration')
		return task.value
	}
	const before = await taskSeconds()
	await action()
	const after = await taskSeconds()
	await session.detach()
	return (after - before) * 1000
}

/** Seven samples: the middle one. */
function median(values: number[]): number {
	const sorted = [...values].sort((a, b) => a - b)
	return sorted[Math.floor(sorted.length / 2)]!
}

test('switching tabs and themes is quick with 300 tabs and 16 favourites', async () => {
	test.setTimeout(300_000)
	const zing = await launchZing()
	const chrome = zing.chrome
	for (let i = 1; i <= 16; i++) await addFavourite(chrome, `Fav${i}`)
	await chrome.evaluate((url) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'add-tabs', count: 300, url }), `${base}/many-`)
	await expect.poll(() => rows(chrome).count()).toBeGreaterThanOrEqual(300)

	const { activeId } = await tabsOf(chrome)
	// Favourites hang as ribbons and have no row in the card list, so only rows can be switched to by id.
	const rowIds = (await rows(chrome).evaluateAll((els) => els.map((el) => (el as unknown as { getAttribute(a: string): string | null }).getAttribute('data-tab-id')))).filter(
		(id): id is string => id !== null && id !== activeId,
	)
	const targets = rowIds.filter((_, i) => i % 40 === 0).slice(0, 7)
	expect(targets).toHaveLength(7)
	const tabTimes: number[] = []
	for (const target of targets) tabTimes.push(await timeTabSwitch(chrome, target))
	const tabMedian = median(tabTimes)
	expect(tabMedian).toBeLessThan(250)

	const dark = await chrome.evaluate(() => (globalThis as unknown as { matchMedia(q: string): { matches: boolean } }).matchMedia('(prefers-color-scheme: dark)').matches)
	// Each switch changes the accent, so the seven themes are the presets after the default, Amber.
	const themeTimes: number[] = []
	for (const theme of accentThemes.slice(1)) {
		const expected = accentVariables(theme, DEFAULT_CUSTOM_COLOR, dark)['--accent']!
		themeTimes.push(await timeThemeSwitch(chrome, theme, expected))
	}
	const themeMedian = median(themeTimes)
	expect(themeMedian).toBeLessThan(250)

	const list = chrome.locator('.rail-tabs')
	await list.evaluate((el) => {
		el.scrollTop = 0
	})
	const railBox = (await list.boundingBox())!
	const row = rows(chrome)
	const held = await row.nth(0).getAttribute('data-tab-id')
	const from = (await row.nth(0).boundingBox())!
	const dragMs = await workOf(chrome, async () => {
		await chrome.mouse.move(from.x + 40, from.y + from.height / 2)
		await chrome.mouse.down()
		await chrome.mouse.move(from.x + 40, railBox.y + railBox.height - 20, { steps: 60 })
		await chrome.mouse.up()
	})
	await expect.poll(() => row.nth(0).getAttribute('data-tab-id')).not.toBe(held)

	await chrome.mouse.move(railBox.x + railBox.width / 2, railBox.y + railBox.height / 2)
	const scrollMs = await workOf(chrome, async () => {
		for (let i = 0; i < 30; i++) await chrome.mouse.wheel(0, 300)
		await expect.poll(() => list.evaluate((el) => el.scrollTop)).toBeGreaterThan(0)
	})

	const annotations = [
		{ type: 'tab switch median', description: `${tabMedian.toFixed(0)} ms (${tabTimes.map((t) => t.toFixed(0)).join(', ')})` },
		{ type: 'theme switch median', description: `${themeMedian.toFixed(0)} ms (${themeTimes.map((t) => t.toFixed(0)).join(', ')})` },
		{ type: 'drag work', description: `${dragMs.toFixed(0)} ms` },
		{ type: 'scroll work', description: `${scrollMs.toFixed(0)} ms` },
	]
	test.info().annotations.push(...annotations)
	console.log(annotations.map(({ type, description }) => `${type}: ${description}`).join('\n'))
	await zing.close()
})

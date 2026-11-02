// Phase 4 PR 1: the tab rail on the side (docs/phase-4.md).
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, layers } from './zing'

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

async function openPage(chrome: Page, title: string): Promise<void> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}/${title}`])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.title).toBe(title)
}

const rows = (chrome: Page) => chrome.locator('.rail-tabs [role="tab"]')
const rowTitles = async (chrome: Page) => (await rows(chrome).locator('.rail-row-title').allInnerTexts()).filter((t) => t !== 'New Tab')

/** The page on screen, as the window really lays it out. */
async function shownPage(chrome: Page, title: string) {
	return (await layers(chrome)).find((l) => l.url === `${base}/${title}` && l.visible)
}

test('tabs sit in a rail on the left by default, with the page beside it', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	await expect(zing.chrome.getByRole('navigation', { name: 'Tab rail' })).toBeVisible()
	await expect(zing.chrome.locator('.tabstrip')).toHaveCount(0)
	await expect.poll(() => rowTitles(zing.chrome)).toContain('Alpha')
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(240)
	await zing.close()
})

test('switching between top, left and right keeps the tabs and moves the page', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	await openPage(zing.chrome, 'Beta')
	const ids = async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.id)
	const before = await ids()
	const window_ = (await layers(zing.chrome)).find((l) => l.url.endsWith('#chrome'))!

	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
	await expect(zing.chrome.locator('.tabstrip')).toBeVisible()
	// On top the app sidebar is its own column again.
	await expect.poll(async () => (await shownPage(zing.chrome, 'Beta'))?.x).toBe(204)

	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'right'))
	await expect(zing.chrome.getByRole('navigation', { name: 'Tab rail' })).toBeVisible()
	await expect.poll(async () => (await shownPage(zing.chrome, 'Beta'))?.width).toBe(window_.width - 204 - 240)

	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'left'))
	await expect.poll(async () => (await shownPage(zing.chrome, 'Beta'))?.x).toBe(240)
	expect(await ids()).toEqual(before)
	for (const page of await layers(zing.chrome)) {
		expect(Number.isInteger(page.x) && Number.isInteger(page.y), page.url).toBe(true)
	}
	await zing.close()
})

test('dragging a row up and down the rail moves its tab', async () => {
	const zing = await launchZing()
	for (const title of ['Zebra', 'Apple', 'Mango']) await openPage(zing.chrome, title)
	const named = async () => (await rowTitles(zing.chrome)).filter((t) => ['Zebra', 'Apple', 'Mango'].includes(t))
	await expect.poll(named).toEqual(['Zebra', 'Apple', 'Mango'])
	const from = (await rows(zing.chrome).filter({ hasText: 'Zebra' }).boundingBox())!
	const to = (await rows(zing.chrome).filter({ hasText: 'Mango' }).boundingBox())!
	await zing.chrome.mouse.move(from.x + 40, from.y + from.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(to.x + 40, to.y + to.height - 4, { steps: 12 })
	await zing.chrome.mouse.up()
	await expect.poll(named).toEqual(['Apple', 'Mango', 'Zebra'])
	await zing.close()
})

test('dragging the rail’s edge resizes it, and the page follows', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	const edge = (await zing.chrome.getByRole('separator', { name: 'Resize tabs' }).boundingBox())!
	await zing.chrome.mouse.move(edge.x + edge.width / 2, edge.y + 200)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(edge.x + edge.width / 2 + 60, edge.y + 200, { steps: 6 })
	await zing.chrome.mouse.up()
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getSettings())).verticalTabWidth).toBe(300)
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(300)
	await zing.close()
})

test('hiding the sidebar gives the rail’s column to the page', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(240)
	await zing.chrome.getByRole('button', { name: 'Hide sidebar (Ctrl+S)' }).click()
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(0)
	await expect(zing.chrome.getByRole('navigation', { name: 'Tab rail' })).toHaveCount(0)
	await zing.chrome.evaluate(() => window.zing.setSetting('sidebarPinned', true))
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(240)
	await zing.close()
})

test('collapsed, the rail is a column of icons and the page takes the rest', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	await zing.chrome.getByRole('button', { name: 'Collapse the tab sidebar to icons' }).click()
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(48)
	const rail = zing.chrome.getByRole('navigation', { name: 'Tab rail' })
	await expect.poll(async () => (await rail.boundingBox())?.width).toBe(48)
	await expect(rail.locator('.rail-row-title').first()).toBeHidden()
	// Each row is still there, as its icon, with its title in the tooltip.
	await expect(zing.chrome.locator('.rail-tabs [role="tab"][title*="Alpha"]')).toHaveCount(1)
	await zing.chrome.getByRole('button', { name: 'Expand the tab sidebar' }).click()
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(240)
	await zing.close()
})

test('three hundred tabs: every one is in the rail, rows out of view skip rendering, and switching stays quick', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate((url) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'add-tabs', count: 300, url }), `${base}/many-`)
	await expect(rows(zing.chrome)).toHaveCount(301)
	expect(await rows(zing.chrome).last().evaluate((row) => (globalThis as unknown as { getComputedStyle(e: unknown): { contentVisibility: string } }).getComputedStyle(row).contentVisibility)).toBe('auto')
	const last = (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.at(-1)!.id
	const elapsed = await zing.chrome.evaluate(async (id) => {
		const started = performance.now()
		await window.zing.activateTab(id)
		const doc = (globalThis as unknown as { document: { querySelector(s: string): { getAttribute(a: string): string | null } | null } }).document
		while (doc.querySelector(`[data-tab-id="${id}"]`)?.getAttribute('aria-selected') !== 'true') await new Promise((r) => setTimeout(r, 5))
		return performance.now() - started
	}, last)
	// From the call to the rail showing the new tab: the bridge both ways plus a render of 301 rows (PLAN.md's switch budget is 50 ms for the page itself).
	expect(elapsed).toBeLessThan(250)
	await zing.close()
})

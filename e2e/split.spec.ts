// Phase 4 PR 3: split view (docs/phase-4.md, part C).
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, layers, pageWhere } from './zing'

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

async function openPage(chrome: Page, title: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await tabsOf(chrome)
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}/${title}`])
	await expect.poll(async () => (await tabsOf(chrome)).tabs.find((t) => t.id === activeId)?.title).toBe(title)
	return activeId!
}

/** The pages on screen, as the window really lays them out, left to right. */
async function shownPages(chrome: Page) {
	return (await layers(chrome)).filter((l) => l.visible && l.url.startsWith(base)).sort((a, b) => a.x - b.x || a.y - b.y)
}

test('a split shows both pages side by side, laid out as the host says, with the new pane in front', async () => {
	const zing = await launchZing()
	const left = await openPage(zing.chrome, 'Left')
	const right = await openPage(zing.chrome, 'Right')
	await zing.chrome.evaluate(([a, b]) => window.zing.splitWith(a!, b!), [left, right])
	await expect.poll(async () => (await shownPages(zing.chrome)).map((l) => l.url)).toEqual([`${base}/Left`, `${base}/Right`])
	const state = await tabsOf(zing.chrome)
	expect(state.activeId).toBe(right)
	const [a, b] = await shownPages(zing.chrome)
	expect(state.shownSplit?.rects.map((r) => [r.x, r.y, r.width, r.height])).toEqual([a, b].map((l) => [l!.x, l!.y, l!.width, l!.height]))
	// The divider sits in the gap between the panes.
	expect(b!.x - (a!.x + a!.width)).toBe(4)
	await expect(zing.chrome.getByRole('separator', { name: 'Resize split' })).toBeVisible()
	// The rail shows the split once, as a group.
	await expect(zing.chrome.getByRole('group', { name: 'Split view' }).getByRole('tab')).toHaveCount(2)
	await zing.close()
})

test('dragging the divider resizes the two panes', async () => {
	const zing = await launchZing()
	const left = await openPage(zing.chrome, 'Left')
	const right = await openPage(zing.chrome, 'Right')
	await zing.chrome.evaluate(([a, b]) => window.zing.splitWith(a!, b!), [left, right])
	await expect.poll(async () => (await shownPages(zing.chrome)).length).toBe(2)
	const before = (await shownPages(zing.chrome))[0]!.width
	const divider = (await zing.chrome.getByRole('separator', { name: 'Resize split' }).boundingBox())!
	await zing.chrome.mouse.move(divider.x + 2, divider.y + 100)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(divider.x + 102, divider.y + 100, { steps: 8 })
	await zing.chrome.mouse.up()
	await expect.poll(async () => (await shownPages(zing.chrome))[0]!.width).toBeGreaterThan(before + 80)
	await zing.close()
})

test('Split with New Tab puts a blank tab beside the page, and closing a pane leaves the other alone', async () => {
	const zing = await launchZing()
	const page = await openPage(zing.chrome, 'Page')
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.split'))
	await expect.poll(async () => (await tabsOf(zing.chrome)).shownSplit?.rects.length).toBe(2)
	const created = (await tabsOf(zing.chrome)).activeId!
	expect(created).not.toBe(page)
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), created)
	await expect.poll(async () => (await tabsOf(zing.chrome)).shownSplit).toBeUndefined()
	expect((await tabsOf(zing.chrome)).splits).toEqual([])
	const [alone] = await shownPages(zing.chrome)
	expect(alone?.url).toBe(`${base}/Page`)
	expect(alone?.x).toBe(240)
	await zing.close()
})

test('a third tab joins the split, and Remove from Split takes one out again', async () => {
	const zing = await launchZing()
	const a = await openPage(zing.chrome, 'One')
	const b = await openPage(zing.chrome, 'Two')
	const c = await openPage(zing.chrome, 'Three')
	await zing.chrome.evaluate(([x, y]) => window.zing.splitWith(x!, y!), [a, b])
	await zing.chrome.evaluate(([x, y]) => window.zing.splitWith(x!, y!), [a, c])
	await expect.poll(async () => (await shownPages(zing.chrome)).length).toBe(3)
	await zing.chrome.evaluate((id) => window.zing.removeFromSplit(id), b)
	await expect.poll(async () => (await shownPages(zing.chrome)).map((l) => l.url)).toEqual([`${base}/One`, `${base}/Three`])
	await zing.close()
})

test('a pane on screen never sleeps, though it is not the active tab', async () => {
	const zing = await launchZing({ env: { ZING_TEST_CLOCK: '1' } })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const a = await openPage(zing.chrome, 'Left')
	const b = await openPage(zing.chrome, 'Right')
	await zing.chrome.evaluate(([x, y]) => window.zing.splitWith(x!, y!), [a, b])
	await zing.chrome.evaluate((ms) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'advance-clock', ms }), 3 * 60_000)
	await zing.chrome.waitForTimeout(1000)
	expect((await tabsOf(zing.chrome)).tabs.find((t) => t.id === a)?.sleeping).not.toBe(true)
	expect((await shownPages(zing.chrome)).length).toBe(2)
	await zing.close()
})

test('dragging a tab onto the page’s right edge highlights that half, and letting go splits it there', async () => {
	const zing = await launchZing()
	const dragged = await openPage(zing.chrome, 'Dragged')
	const page = await openPage(zing.chrome, 'Page')
	const area = (await tabsOf(zing.chrome)).pageArea!
	const row = (await zing.chrome.locator('.rail-tabs [role="tab"]').filter({ hasText: 'Dragged' }).boundingBox())!
	await zing.chrome.mouse.move(row.x + 40, row.y + row.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(area.x + area.width - 20, area.y + area.height / 2, { steps: 12 })
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.locator('.split-drop-zone')).toBeVisible()
	await zing.chrome.mouse.up()
	await expect.poll(async () => (await tabsOf(zing.chrome)).splits?.[0]?.tabIds).toEqual([page, dragged])
	await expect(surface.locator('.split-drop-zone')).toHaveCount(0)
	await expect.poll(async () => (await shownPages(zing.chrome)).map((l) => l.url)).toEqual([`${base}/Page`, `${base}/Dragged`])
	await zing.close()
})

test('with Reopen tabs on, a split comes back after a relaunch', async () => {
	const first = await launchZing()
	await first.chrome.evaluate(() => window.zing.setSetting('restoreTabs', true))
	const a = await openPage(first.chrome, 'Left')
	const b = await openPage(first.chrome, 'Right')
	await first.chrome.evaluate(([x, y]) => window.zing.splitWith(x!, y!), [a, b])
	await expect.poll(async () => (await tabsOf(first.chrome)).splits?.length).toBe(1)
	await first.close()

	const second = await launchZing({ userDataDir: first.userDataDir })
	await expect.poll(async () => (await tabsOf(second.chrome)).splits?.[0]?.tabIds.length).toBe(2)
	await expect.poll(async () => (await shownPages(second.chrome)).map((l) => l.url)).toEqual([`${base}/Left`, `${base}/Right`])
	await second.close()
})

test('split tabs don\'t overlap, and the active one stays inside the box', async () => {
	const zing = await launchZing()
	const left = await openPage(zing.chrome, 'Left')
	const right = await openPage(zing.chrome, 'Right')
	await zing.chrome.evaluate(([a, b]) => window.zing.splitWith(a!, b!), [left, right])
	const group = zing.chrome.getByRole('group', { name: 'Split view' })
	await expect(group.getByRole('tab')).toHaveCount(2)
	const box = (await group.boundingBox())!
	const rows = await group.getByRole('tab').evaluateAll((els) => {
		const dom = globalThis as unknown as { getComputedStyle(e: unknown): { marginTop: string; marginBottom: string; boxShadow: string } }
		const nodes = els as unknown as { getAttribute(name: string): string | null; getBoundingClientRect(): { top: number; bottom: number; left: number; right: number } }[]
		return nodes.map((el) => {
			const rect = el.getBoundingClientRect()
			const style = dom.getComputedStyle(el)
			return { active: el.getAttribute('aria-selected') === 'true', top: rect.top, bottom: rect.bottom, left: rect.left, right: rect.right, marginTop: style.marginTop, marginBottom: style.marginBottom, shadow: style.boxShadow }
		})
	})
	for (const row of rows) {
		expect(row.marginTop).toBe('0px')
		expect(row.marginBottom).toBe('0px')
		expect(row.top).toBeGreaterThanOrEqual(box.y)
		expect(row.bottom).toBeLessThanOrEqual(box.y + box.height)
		expect(row.left).toBeGreaterThanOrEqual(box.x)
		expect(row.right).toBeLessThanOrEqual(box.x + box.width)
	}
	expect(rows[0]!.bottom).toBeLessThanOrEqual(rows[1]!.top)
	const active = rows.find((row) => row.active)!
	expect(active.shadow).toBe('none')
	await zing.close()
})

test('on the right, a split’s active pane stays inside the box', async () => {
	const zing = await launchZing()
	const left = await openPage(zing.chrome, 'Left')
	const right = await openPage(zing.chrome, 'Right')
	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'right'))
	await zing.chrome.evaluate(([a, b]) => window.zing.splitWith(a!, b!), [left, right])
	const group = zing.chrome.getByRole('group', { name: 'Split view' })
	await expect(group.getByRole('tab')).toHaveCount(2)
	const box = (await group.boundingBox())!
	const active = await group.getByRole('tab', { selected: true }).evaluate((el) => {
		const rect = (el as unknown as { getBoundingClientRect(): { left: number; right: number } }).getBoundingClientRect()
		const style = (globalThis as unknown as { getComputedStyle(e: unknown): { marginLeft: string; marginRight: string } }).getComputedStyle(el)
		return { left: rect.left, right: rect.right, marginLeft: style.marginLeft, marginRight: style.marginRight }
	})
	expect(active.left).toBeGreaterThanOrEqual(box.x)
	expect(active.right).toBeLessThanOrEqual(box.x + box.width)
	expect(active.marginLeft).toBe('0px')
	expect(active.marginRight).toBe('0px')
	await zing.close()
})

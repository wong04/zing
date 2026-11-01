// Phase 4 PR 3: split view (docs/phase-4.md, part C).
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

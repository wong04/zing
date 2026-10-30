import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, test, type ElectronApplication, type Page } from '@playwright/test'
import type { ZingApi } from '@shared/ipc'
import { launchZing } from './zing'

// The callbacks passed to `evaluate` run in the chrome renderer, which has the preload's bridge.
declare const window: { readonly zing: ZingApi }

let server: Server
let port: number

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const title = decodeURIComponent((req.url ?? '/').slice(1)) || 'Home'
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>${title}</title><body>${title}</body>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	port = (server.address() as AddressInfo).port
})

test.afterAll(() => server.close())

const url = (host: string, title: string): string => `http://${host}.localhost:${port}/${encodeURIComponent(title)}`

interface Tab {
	id: string
	title: string
	pinned: boolean
}

async function tabs(chrome: Page): Promise<Tab[]> {
	return chrome.evaluate(async () => (await window.zing.getTabs()).tabs.map((t) => ({ id: t.id, title: t.title, pinned: t.pinned })))
}

async function organization(chrome: Page): Promise<string> {
	return chrome.evaluate(async () => (await window.zing.getTabs()).organization)
}

/** Opens a tab on a page with this title and waits until the strip shows it. */
async function openPage(chrome: Page, host: string, title: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const [id] = (await tabs(chrome)).slice(-1).map((t) => t.id)
	await chrome.evaluate(([tabId, address]) => window.zing.navigateTab(tabId!, address!), [id, url(host, title)])
	await expect.poll(async () => (await tabs(chrome)).some((t) => t.id === id && t.title === title)).toBe(true)
	return id!
}

async function chipOrder(chrome: Page): Promise<string[]> {
	return chrome.locator('.tabs > [role="tab"]').evaluateAll((els) => els.map((e) => e.getAttribute('data-tab-id') ?? e.getAttribute('data-stack-key') ?? ''))
}

async function dragChip(chrome: Page, from: string, to: string): Promise<void> {
	const a = await chrome.locator(from).boundingBox()
	const b = await chrome.locator(to).boundingBox()
	if (!a || !b) throw new Error('chips are not on screen')
	const y = a.y + a.height / 2
	await chrome.mouse.move(a.x + a.width / 2, y)
	await chrome.mouse.down()
	await chrome.mouse.move(a.x + a.width / 2 + 10, y, { steps: 3 })
	await chrome.mouse.move(b.x + b.width / 2 + 10, y, { steps: 12 })
	await chrome.mouse.up()
}

async function windowX(app: ElectronApplication): Promise<number> {
	return app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]!.getBounds().x)
}

test('pinning moves a tab to an icon-only chip behind a divider, and unpinning brings it back', async () => {
	const { app, chrome } = await launchZing()
	const second = await openPage(chrome, 'one', 'Second')
	await openPage(chrome, 'two', 'Third')

	await chrome.evaluate((id) => window.zing.pinTab(id), second)
	const pinned = chrome.locator(`[data-tab-id="${second}"]`)
	await expect(pinned).toHaveClass(/pinned/)
	await expect(pinned.locator('.tab-title')).toHaveCount(0)
	await expect(chrome.locator('.tab-divider')).toHaveCount(1)
	expect((await chipOrder(chrome))[0]).toBe(second)

	await chrome.evaluate((id) => window.zing.unpinTab(id), second)
	await expect(chrome.locator('.tab-divider')).toHaveCount(0)
	await expect(pinned).not.toHaveClass(/pinned/)
	await app.close()
})

test('dragging a chip reorders live, keeps pinned tabs apart, and never moves the window', async () => {
	const { app, chrome } = await launchZing()
	const a = await openPage(chrome, 'one', 'Alpha')
	const b = await openPage(chrome, 'two', 'Beta')
	const c = await openPage(chrome, 'three', 'Gamma')
	await chrome.evaluate((id) => window.zing.pinTab(id), c)
	const before = await windowX(app)
	const loose = (await chipOrder(chrome)).filter((id) => id !== c)

	await dragChip(chrome, `[data-tab-id="${loose[0]}"]`, `[data-tab-id="${loose[loose.length - 1]}"]`)
	await expect.poll(() => chipOrder(chrome)).toEqual([c, ...loose.slice(1), loose[0]!])

	// A loose chip dragged over the pinned run stays behind it.
	await dragChip(chrome, `[data-tab-id="${b}"]`, `[data-tab-id="${c}"]`)
	expect((await chipOrder(chrome))[0]).toBe(c)
	expect(await windowX(app)).toBe(before)
	expect(new Set(await chipOrder(chrome))).toEqual(new Set(loose.concat(c)))
	expect([a, b].every((id) => loose.includes(id))).toBe(true)
	await app.close()
})

test('arrangements sort the strip, and dragging out of a sorted one switches to manual', async () => {
	const { app, chrome } = await launchZing()
	await openPage(chrome, 'one', 'Zebra')
	await openPage(chrome, 'two', 'Apple')
	await openPage(chrome, 'three', 'Mango')

	await chrome.evaluate(() => window.zing.setTabOrganization('alphabetical'))
	await expect
		.poll(async () => (await chrome.locator('.tabs > [role="tab"]').evaluateAll((els) => els.map((e) => e.getAttribute('title')))).filter((t) => t !== 'New Tab'))
		.toEqual(['Apple', 'Mango', 'Zebra'])

	await chrome.evaluate(() => window.zing.setTabOrganization('newest'))
	await expect
		.poll(async () => (await chrome.locator('.tabs > [role="tab"]').evaluateAll((els) => els.map((e) => e.getAttribute('title')))).slice(0, 3))
		.toEqual(['Mango', 'Apple', 'Zebra'])

	const order = await chipOrder(chrome)
	await dragChip(chrome, `[data-tab-id="${order[0]}"]`, `[data-tab-id="${order[2]}"]`)
	await expect.poll(() => organization(chrome)).toBe('manual')
	await expect.poll(() => chipOrder(chrome)).toEqual([order[1]!, order[2]!, order[0]!, order[3]!])
	await app.close()
})

test('Dropdown style stacks a site and lists its pages in the overlay', async () => {
	const { app, chrome } = await launchZing()
	const first = await openPage(chrome, 'a.site', 'Inbox')
	await openPage(chrome, 'b.site', 'Docs')
	await openPage(chrome, 'solo', 'Alone')

	await chrome.evaluate(() => window.zing.setTabDropdowns(true))
	const stack = chrome.locator('.tab.stack')
	await expect(stack).toHaveCount(1)
	await expect(stack.locator('.stack-count')).toHaveText('2')

	await stack.click()
	const overlay = await app.waitForEvent('window', { predicate: (p) => p.url().endsWith('#overlay-stack') }).catch(() => undefined)
	const dropdown = overlay ?? app.windows().find((p) => p.url().endsWith('#overlay-stack'))
	if (!dropdown) throw new Error('the stack dropdown view did not open')
	await expect(dropdown.locator('.stack-row')).toHaveCount(2)
	await expect(chrome.locator('.tab.stack.open')).toHaveCount(1)

	await dropdown.locator(`.stack-row[data-tab-id="${first}"]`).click()
	await expect.poll(async () => chrome.evaluate(async () => (await window.zing.getTabs()).activeId)).toBe(first)
	await expect(chrome.locator('.tab.stack.open')).toHaveCount(0)
	await app.close()
})

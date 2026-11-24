import { readFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Browser, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { connectMcp, textOf, type McpClient } from './mcp'
import { launchZing, layers, pageWhere, test, viewAt, type Layer } from './zing'

declare const window: { readonly zing: ZingApi }

// The share UI driven as the owner would: the tab menu, the strip's Stop, the confirm bar's buttons, Settings' Regenerate.

const FIXTURES = join(import.meta.dirname, 'fixtures', 'ai-act')

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		if (req.method === 'POST') {
			req.resume()
			req.on('end', () => res.end('<!doctype html><title>sent</title><p>Sent</p>'))
			return
		}
		const name = /^\/([a-z-]+)$/.exec(req.url ?? '')?.[1]
		try {
			res.end(readFileSync(join(FIXTURES, `${name}.html`)))
		} catch {
			res.end('<!doctype html><title>missing</title>')
		}
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

function post(chrome: Page, message: Record<string, unknown>): Promise<void> {
	return chrome.evaluate((m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', ...m }), message)
}

async function openTab(chrome: Page, path: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}${path}`])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.url).toBe(`${base}${path}`)
	return activeId!
}

async function openMenu(chrome: Page, id: string): Promise<void> {
	await chrome.evaluate((tab) => window.zing.openTabMenu(tab, { x: 40, y: 40 }), id)
}

/** Shares the tab from its tab menu, as the owner would. */
async function shareFromMenu(zing: { chrome: Page; menus(): (string | null)[][] }, id: string): Promise<void> {
	const before = zing.menus().length
	await openMenu(zing.chrome, id)
	await expect.poll(() => zing.menus().length).toBe(before + 1)
	expect(zing.menus().at(-1)).toContain('Let AI use this tab')
	await post(zing.chrome, { action: 'menu-choose', label: 'Let AI use this tab' })
}

async function sharedTabs(mcp: McpClient): Promise<{ tab: string }[]> {
	return JSON.parse(textOf(await mcp.call('list_tabs'))) as { tab: string }[]
}

/** The ref of the first snapshot line matching `pattern`. */
async function refOf(mcp: McpClient, tab: string, pattern: RegExp): Promise<string> {
	let ref: string | undefined
	await expect.poll(async () => {
		const line = textOf(await mcp.call('snapshot', { tab })).split('\n').find((l) => pattern.test(l))
		ref = line ? /\[(e\d+)\]/.exec(line)?.[1] : undefined
		return ref
	}).toBeDefined()
	return ref!
}

const view = (browser: Browser, hash: string): Promise<Page> => pageWhere(browser, (p) => p.url().endsWith(hash))

test('the context menu shares a tab and draws the outline', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const id = await openTab(zing.chrome, '/react-form')
	await shareFromMenu(zing, id)
	await expect.poll(async () => (await sharedTabs(mcp)).length).toBe(1)
	await expect(zing.chrome.locator(`[data-tab-id="${id}"]`).first()).toHaveClass(/ai-shared/)
	const strip = await view(zing.browser, '#ai-strip')
	await expect(strip.getByText('AI is using this tab')).toBeVisible()
	// Shared, the menu offers to stop instead.
	await openMenu(zing.chrome, id)
	await expect.poll(() => zing.menus().at(-1)).toContain('Stop AI using this tab')
	expect(zing.menus().at(-1)).not.toContain('Let AI use this tab')
	await zing.close()
})

test('zing pages have no share item', async () => {
	const zing = await launchZing()
	await connectMcp(zing.chrome)
	const { activeId } = await zing.chrome.evaluate(() => window.zing.getTabs())
	await openMenu(zing.chrome, activeId!)
	await expect.poll(() => zing.menus().length).toBe(1)
	expect(zing.menus()[0]!.some((label) => label?.includes('AI') === true)).toBe(false)
	await zing.close()
})

test('Stop unshares and denies a waiting confirm', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const id = await openTab(zing.chrome, '/react-form')
	await shareFromMenu(zing, id)
	let tab = ''
	await expect.poll(async () => (tab = (await sharedTabs(mcp))[0]?.tab ?? '')).not.toBe('')
	const click = mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Send"/) })
	const confirm = await view(zing.browser, '#ai-confirm')
	await expect(confirm.getByText('AI wants to click', { exact: false })).toBeVisible({ timeout: 15_000 })
	await expect(confirm.getByRole('button', { name: 'Allow', exact: true })).toBeVisible()
	const strip = await view(zing.browser, '#ai-strip')
	// A bar's view drops DevTools presses (moves go through) for a moment after the bars change; a person's click isn't dropped (the native-click test below). The click goes again until Stop lands.
	await expect(async () => {
		await strip.getByRole('button', { name: 'Stop', exact: true }).click({ timeout: 1_000 })
		expect(await sharedTabs(mcp)).toEqual([])
	}).toPass({ timeout: 15_000 })
	expect((await click).isError).toBe(true)
	expect(await sharedTabs(mcp)).toEqual([])
	await expect(zing.chrome.locator(`[data-tab-id="${id}"]`).first()).not.toHaveClass(/ai-shared/)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getAiState())).confirm).toBeUndefined()
	await zing.close()
})

test('a waiting confirm on a background tab badges it', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const id = await openTab(zing.chrome, '/react-form')
	await shareFromMenu(zing, id)
	let tab = ''
	await expect.poll(async () => (tab = (await sharedTabs(mcp))[0]?.tab ?? '')).not.toBe('')
	const click = mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Send"/) })
	const confirm = await view(zing.browser, '#ai-confirm')
	await expect(confirm.getByText('AI wants to click', { exact: false })).toBeVisible({ timeout: 15_000 })
	// Another tab in front: the confirm waits on the shared one, which is badged.
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const chip = zing.chrome.locator(`[data-tab-id="${id}"]`).first()
	await expect(chip).toHaveClass(/ai-waiting/)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getAiState())).confirm).toBeUndefined()
	// Back in front, its bar is up again, and Deny answers it.
	await zing.chrome.evaluate((tabId) => window.zing.activateTab(tabId), id)
	await expect(chip).not.toHaveClass(/ai-waiting/)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getAiState())).confirm?.tab).toBe(id)
	// A view just shown again drops DevTools presses (moves go through) for a moment, frames or not; a person's click isn't dropped. The click goes again until Deny lands.
	await expect(async () => {
		await confirm.getByRole('button', { name: 'Deny' }).click({ timeout: 1_000 })
		expect((await zing.chrome.evaluate(() => window.zing.getAiState())).confirm).toBeUndefined()
	}).toPass({ timeout: 15_000 })
	expect((await click).structuredContent?.code).toBe('Denied')
	await zing.close()
})

test("the owner's first mouse click on a confirm bar just shown again lands", async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const id = await openTab(zing.chrome, '/react-form')
	await shareFromMenu(zing, id)
	let tab = ''
	await expect.poll(async () => (tab = (await sharedTabs(mcp))[0]?.tab ?? '')).not.toBe('')
	const click = mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Send"/) })
	const confirm = await view(zing.browser, '#ai-confirm')
	await expect(confirm.getByText('AI wants to click', { exact: false })).toBeVisible({ timeout: 15_000 })
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getAiState())).confirm).toBeUndefined()
	await zing.chrome.evaluate((tabId) => window.zing.activateTab(tabId), id)
	let bar: Layer | undefined
	await expect.poll(async () => (bar = (await layers(zing.chrome)).find((l) => l.url.endsWith('#ai-confirm') && l.visible))).toBeDefined()
	const button = (await confirm.getByRole('button', { name: 'Deny' }).boundingBox())!
	const at = { x: bar!.x + button.x + button.width / 2, y: bar!.y + button.y + button.height / 2 }
	await expect.poll(() => viewAt(zing.chrome, at)).toMatch(/#ai-confirm$/)
	// One click, as a person makes it: Windows' mouse messages to the window under the pointer.
	await post(zing.chrome, { action: 'native-click', ...at })
	expect((await click).structuredContent?.code).toBe('Denied')
	await zing.close()
})

test('regenerating the token cuts off the old one', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	expect(await sharedTabs(mcp)).toEqual([])
	// Settings' state never carries the token: the sheet gets it only on Show.
	const token = await zing.chrome.evaluate(() => window.zing.showAiToken())
	const settings = JSON.stringify(await zing.chrome.evaluate(() => window.zing.getAiSettings()))
	expect(settings).not.toContain(token)
	expect(settings).toContain('Bearer <click Show>')
	await zing.chrome.evaluate(() => window.zing.openSettings('advanced'))
	const sheet = await view(zing.browser, '#surface')
	await sheet.getByRole('button', { name: 'Regenerate' }).click()
	await expect.poll(() => mcp.call('list_tabs').then(() => 'answered', (error: Error) => error.message)).not.toBe('answered')
	await zing.close()
})

import { execFile } from 'node:child_process'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { promisify } from 'node:util'
import { expect, test, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, layers, pageWhere, pages, type Launched, type Layer } from './zing'

declare const window: { readonly zing: ZingApi }

// The real-input tier: OS keyboard and mouse input (SendInput) into an on-screen window, the paths
// CDP input never takes (accelerators, focus handoff between HWNDs, clicks on an overlay popup).
// It takes over the PC's mouse and keyboard, so it runs only by hand on a PC nobody is using
// (`npm run test:e2e:real-input`); normal runs leave it out (playwright.config.ts).

const run = promisify(execFile)
const SCRIPT = join(import.meta.dirname, 'real-input.ps1')
const ON_SCREEN = { ZING_E2E_ONSCREEN: '1' }

let server: Server
let port = 0

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const title = decodeURIComponent((req.url ?? '/').slice(1)) || 'Home'
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>${title}</title><body style="height:100vh">${title}</body>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	port = (server.address() as AddressInfo).port
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const url = (host: string, title: string): string => `http://${host}.localhost:${port}/${encodeURIComponent(title)}`

/** Brings Zing to the front and sends real input: a key chord ("ctrl+t") or a left click at a point of the window's content in DIPs. */
async function realInput(zing: Launched, input: { keys: string } | { x: number; y: number }): Promise<void> {
	const args = ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', SCRIPT, '-ProcessId', String(zing.pid)]
	if ('keys' in input) args.push('-Keys', input.keys)
	else args.push('-X', String(input.x), '-Y', String(input.y))
	await run('powershell.exe', args)
}

const tabCount = async (chrome: Page): Promise<number> => (await chrome.evaluate(() => window.zing.getTabs())).tabs.length

/** Some view of Zing has the keyboard, so a key a person presses reaches Zing's shortcuts. */
async function viewHasFocus(zing: Launched): Promise<boolean> {
	for (const page of pages(zing.browser)) {
		if (await page.evaluate(() => document.hasFocus()).catch(() => false)) return true
	}
	return false
}

/** Opens a tab on a page with this title through the API and waits until the strip shows it. */
async function openPage(chrome: Page, host: string, title: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, address]) => window.zing.navigateTab(id!, address!), [activeId!, url(host, title)])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.title).toBe(title)
	return activeId!
}

/** The window's visible view whose address ends so, once it shows (TestHooks "layers"). */
async function shownLayer(chrome: Page, suffix: string): Promise<Layer> {
	let found: Layer | undefined
	await expect
		.poll(async () => {
			found = (await layers(chrome)).find((l) => l.url.endsWith(suffix) && l.visible)
			return found !== undefined
		})
		.toBe(true)
	return found!
}

const center = (layer: Layer, box: { x: number; y: number; width: number; height: number } = { x: 0, y: 0, width: layer.width, height: layer.height }) => ({
	x: layer.x + box.x + box.width / 2,
	y: layer.y + box.y + box.height / 2,
})

test('Ctrl+T pressed on the keyboard opens a tab', { tag: '@real-input' }, async () => {
	const zing = await launchZing({ env: ON_SCREEN })
	await expect.poll(() => tabCount(zing.chrome)).toBeGreaterThan(0)
	await expect.poll(() => viewHasFocus(zing)).toBe(true)
	const before = await tabCount(zing.chrome)

	await realInput(zing, { keys: 'ctrl+t' })
	await expect.poll(() => tabCount(zing.chrome)).toBe(before + 1)
	await zing.close()
})

test('closing the focused tab hands the keyboard to a view, so shortcuts still work', { tag: '@real-input' }, async () => {
	const zing = await launchZing({ env: ON_SCREEN })
	await openPage(zing.chrome, 'focus', 'Focused')
	// A real click puts the keyboard in the page's own view.
	await realInput(zing, center(await shownLayer(zing.chrome, '/Focused')))
	const focused = await pageWhere(zing.browser, (p) => p.url().endsWith('/Focused'))
	await expect.poll(() => focused.evaluate(() => document.hasFocus())).toBe(true)
	const before = await tabCount(zing.chrome)

	// The focused view closes with its tab; the window must pass the keyboard on, or no shortcut works.
	await realInput(zing, { keys: 'ctrl+w' })
	await expect.poll(() => tabCount(zing.chrome)).toBe(before - 1)
	await expect.poll(() => viewHasFocus(zing)).toBe(true)
	await realInput(zing, { keys: 'ctrl+t' })
	await expect.poll(() => tabCount(zing.chrome)).toBe(before)
	await zing.close()
})

test('a real click on a stack dropdown row switches to that tab', { tag: '@real-input' }, async () => {
	const zing = await launchZing({ env: ON_SCREEN })
	const first = await openPage(zing.chrome, 'a.site', 'Inbox')
	await openPage(zing.chrome, 'b.site', 'Docs')
	await openPage(zing.chrome, 'solo', 'Alone')
	await zing.chrome.evaluate(() => window.zing.setTabDropdowns(true))
	const stack = zing.chrome.locator('.tab.stack')
	await expect(stack).toHaveCount(1)

	await stack.click()
	const dropdown = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay-stack'))
	const row = dropdown.locator(`.stack-row[data-tab-id="${first}"]`)
	await expect(row).toBeVisible()
	const box = (await row.boundingBox())!
	const layer = await shownLayer(zing.chrome, '#overlay-stack')

	// tabs.spec.ts can only dispatch DOM pointer events here: CDP mouse input never reaches this view.
	await realInput(zing, center(layer, box))
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId).toBe(first)
	await expect(zing.chrome.locator('.tab.stack.open')).toHaveCount(0)
	await zing.close()
})

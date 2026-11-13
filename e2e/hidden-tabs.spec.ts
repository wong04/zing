import { readFileSync, existsSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect } from '@playwright/test'
import { CHROME_URL, launchBare, scratchDir, test, type BareZing, type Layer } from './zing'

// Bare runs: Playwright's attach makes every page it touches report itself visible and render, hidden tabs included.

const MINUTE = 60_000

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		// A clock the page keeps itself: it stops while the renderer is suspended and goes on once it resumes.
		res.end(`<!doctype html><title>${req.url!.slice(1)}</title><script>window.ticks = 0; setInterval(() => window.ticks++, 50)</script>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

async function inChrome<T>(zing: BareZing, expression: string): Promise<T> {
	const [value] = await zing.evaluate((url) => url === CHROME_URL, expression)
	return value as T
}

async function hook(zing: BareZing, message: Record<string, unknown>): Promise<void> {
	await inChrome(zing, `chrome.webview.postMessage(${JSON.stringify({ kind: 'test', ...message })})`)
}

async function layerOf(zing: BareZing, path: string): Promise<Layer | undefined> {
	const file = join(scratchDir('zing-layers-'), 'layers.json')
	await hook(zing, { action: 'layers', file })
	await expect.poll(() => existsSync(file)).toBe(true)
	return (JSON.parse(readFileSync(file, 'utf8')) as Layer[]).find((layer) => layer.url === `${base}${path}`)
}

async function openTab(zing: BareZing, path: string): Promise<string> {
	await inChrome(zing, "zing.runCommand('tab.new')")
	const id = await inChrome<string>(zing, 'zing.getTabs().then((s) => s.activeId)')
	await inChrome(zing, `zing.navigateTab(${JSON.stringify(id)}, ${JSON.stringify(`${base}${path}`)})`)
	await expect.poll(async () => (await layerOf(zing, path))?.url).toBe(`${base}${path}`)
	return id
}

const ticks = async (zing: BareZing, path: string) => (await zing.evaluate((url) => url === `${base}${path}`, 'window.ticks'))[0] as number

test('a hidden tab asks for less memory at once, is suspended after five minutes, and runs again when shown', async () => {
	const zing = await launchBare(scratchDir('zing-host-data-'), { ZING_TEST_CLOCK: '1' })
	const left = await openTab(zing, '/left')
	await openTab(zing, '/shown')

	await expect.poll(async () => (await layerOf(zing, '/left'))?.lowMemory).toBe(true)
	expect(await layerOf(zing, '/shown')).toMatchObject({ visible: true, lowMemory: false, suspended: false })
	await hook(zing, { action: 'advance-clock', ms: 4 * MINUTE })
	await new Promise((resolve) => setTimeout(resolve, 1000))
	expect((await layerOf(zing, '/left'))?.suspended).toBe(false)

	await hook(zing, { action: 'advance-clock', ms: 2 * MINUTE })
	await expect.poll(async () => (await layerOf(zing, '/left'))?.suspended).toBe(true)
	expect((await layerOf(zing, '/shown'))?.suspended).toBe(false)

	await inChrome(zing, `zing.activateTab(${JSON.stringify(left)})`)
	await expect.poll(async () => await layerOf(zing, '/left')).toMatchObject({ visible: true, suspended: false, lowMemory: false })
	const before = await ticks(zing, '/left')
	await expect.poll(() => ticks(zing, '/left')).toBeGreaterThan(before + 5)
	expect(await inChrome<boolean>(zing, 'zing.getTabs().then((s) => s.tabs.some((t) => t.sleeping))')).toBe(false)
	await zing.close()
})

test('both panes of a split on screen keep their memory and are never suspended', async () => {
	const zing = await launchBare(scratchDir('zing-host-data-'), { ZING_TEST_CLOCK: '1' })
	const a = await openTab(zing, '/pane-a')
	const b = await openTab(zing, '/pane-b')
	await openTab(zing, '/away')
	await inChrome(zing, `zing.splitWith(${JSON.stringify(a)}, ${JSON.stringify(b)})`)
	await expect.poll(async () => (await layerOf(zing, '/pane-a'))?.visible).toBe(true)
	await expect.poll(async () => (await layerOf(zing, '/away'))?.lowMemory).toBe(true)

	await hook(zing, { action: 'advance-clock', ms: 6 * MINUTE })
	await expect.poll(async () => (await layerOf(zing, '/away'))?.suspended).toBe(true)
	for (const pane of ['/pane-a', '/pane-b']) expect(await layerOf(zing, pane)).toMatchObject({ visible: true, lowMemory: false, suspended: false })
	await zing.close()
})

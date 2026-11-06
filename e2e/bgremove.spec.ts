import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import { drawFixture, encodePng } from '../src/shared/convert/bgFixture.node'
import type { ZingApi } from '../src/shared/ipc'
import { startEngineFixture, type EngineFixture } from './engineFixture'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

// Zing.FakeEngine's file stands in for the model here, so no real model runs and nothing leaves this PC: this spec checks the
// offer, what the host serves and to whom, the worker's life, and that a model that can't be read is said plainly.
// The real model is run by bgremove-real.spec.ts (ZING_REAL_ENGINES=1) and src/shared/convert/bgremove.real.test.ts.

let fixture: EngineFixture
let server: Server | undefined

test.afterEach(async () => {
	await fixture?.close()
	await new Promise<void>((resolve) => (server ? server.close(() => resolve()) : resolve()))
	server = undefined
})

async function openTool(zing: Launched): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await surface.getByTestId('toolbox-tile').getByText('Background Remover', { exact: true }).click()
	const panel = surface.getByRole('dialog', { name: 'Background Remover', exact: true })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

async function queuePick(zing: Launched, ...files: string[]): Promise<void> {
	await zing.chrome.evaluate((paths) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: paths }), files)
}

async function choose(zing: Launched, panel: Locator, file: string): Promise<void> {
	await queuePick(zing, file)
	await panel.getByRole('button', { name: /^Choose / }).click()
	await expect(panel.getByText(file.split('\\').at(-1)!, { exact: true })).toBeVisible()
}

/** The PNGs in Downloads; the folder does not exist until something is saved. */
function savedPngs(zing: Launched): string[] {
	const folder = join(zing.userDataDir, 'downloads')
	return existsSync(folder) ? readdirSync(folder).filter((name) => name.endsWith('.png')) : []
}

function picture(zing: Launched, name: string): string {
	const path = join(zing.userDataDir, name)
	const fixtureImage = drawFixture(64, 48)
	writeFileSync(path, encodePng(fixtureImage.rgba, 64, 48))
	return path
}

/** Every address the browser asked for, in any page or worker of the run. */
function recordRequests(zing: Launched): string[] {
	const urls: string[] = []
	for (const context of zing.browser.contexts()) context.on('request', (request) => urls.push(request.url()))
	return urls
}

test('with no model, the tool states its size and source and downloads only on a click; onnxruntime loads only when a removal runs', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const requested = recordRequests(zing)
	const file = picture(zing, 'cat.png')
	const isOrt = (url: string): boolean => /ort-wasm|bgremove\.worker|onnx/i.test(url)

	expect(requested.filter(isOrt)).toEqual([])
	const { surface, panel } = await openTool(zing)
	await choose(zing, panel, file)
	// The tool is open and a picture chosen; the model's code and WebAssembly are still not loaded.
	expect(requested.filter(isOrt)).toEqual([])

	await panel.getByRole('button', { name: 'Remove background' }).click()
	const offer = panel.getByText(/isn't on this PC yet/)
	await expect(offer).toBeVisible()
	await expect(offer).toContainText('download from http://127.0.0.1')
	await expect(panel.getByText(/Licence: test fixture/)).toBeVisible()
	await expect(panel.getByText(/Nothing downloads until you press Download/)).toBeVisible()
	// Nothing was fetched: not by the host, and the worker has not been asked to load the model.
	expect(fixture.requests.filter((r) => r.includes('isnet'))).toEqual([])
	expect(requested.filter(isOrt)).toEqual([])
	expect(surface.workers()).toHaveLength(0)

	await panel.getByRole('button', { name: /^Download Background remover/ }).click()
	// The fake model is not an ONNX file, so the worker says so plainly, and nothing is saved.
	await expect(panel.getByText(/background model could not be loaded/)).toBeVisible({ timeout: 60_000 })
	expect(fixture.requests.filter((r) => r.includes('isnet'))).toHaveLength(1)
	expect(savedPngs(zing)).toEqual([])
	// The WebAssembly compiled in the worker under the chrome's CSP as it is, from the app's own bundle.
	expect(requested.some((url) => /ort-wasm-simd-threaded.*\.wasm$/.test(url))).toBe(true)
	// Everything the browser asked for was the app's own bundle or the host's file answers (the picture and the model); nothing else.
	expect(requested.filter((url) => !url.startsWith('https://app.zing/') && !url.startsWith('https://local.zing/') && !url.startsWith('data:'))).toEqual([])
	expect(requested.filter((url) => url.startsWith('https://local.zing/')).map((url) => url.replace(/[0-9a-f]{32}/, '<token>'))).toEqual([
		'https://local.zing/zing-local/file/<token>',
		'https://local.zing/zing-local/file/<token>',
		'https://local.zing/zing-local/model/bgmodel',
	])
	// And the worker has ended.
	await expect.poll(() => surface.workers().length).toBe(0)
	await zing.close()
})

test('the host serves the model by its id and a picture by its token to Zing views only, and refuses all else; a page tab gets nothing', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const file = picture(zing, 'tok.png')
	await zing.chrome.evaluate(() => window.zing.ensureEngine('bgmodel'))
	await queuePick(zing, file)
	const token = await zing.chrome.evaluate(async () => (await window.zing.pickFiles(['image']))[0]!.token)

	// The chrome page's own CSP only lets it fetch its own origin; the tool fetches from a worker, which has none. So the
	// probe lifts the page's policy for this test alone (a DevTools command), to ask the same questions the worker does.
	const cdp = await zing.chrome.context().newCDPSession(zing.chrome)
	await cdp.send('Page.setBypassCSP', { enabled: true })
	await zing.chrome.reload()
	await zing.chrome.waitForLoadState('domcontentloaded')
	const local = 'https://local.zing'
	const asked = (path: string, init?: RequestInit): Promise<number | string> =>
		zing.chrome.evaluate(([p, i]) => fetch(p as string, i as RequestInit | undefined).then((r) => r.status, (e: Error) => e.name), [local + path, init] as const)

	const model = await zing.chrome.evaluate(async () => {
		const response = await fetch('https://local.zing/zing-local/model/bgmodel')
		return { status: response.status, length: Number(response.headers.get('Content-Length')), bytes: (await response.arrayBuffer()).byteLength, type: response.headers.get('Content-Type') }
	})
	expect(model).toEqual({ status: 200, length: fixture.bytes, bytes: fixture.bytes, type: 'application/octet-stream' })
	const served = await zing.chrome.evaluate(async (t) => Array.from(new Uint8Array(await (await fetch(`https://local.zing/zing-local/file/${t}`)).arrayBuffer())), token)
	expect(Buffer.from(served).equals(readFileSync(file))).toBe(true)

	expect(await asked('/zing-local/model/ffmpeg')).toBe(403)
	expect(await asked('/zing-local/model/ytdlp')).toBe(403)
	expect(await asked('/zing-local/model/bgmodel/../ffmpeg')).toBe(403)
	expect(await asked('/zing-local/file/' + '0'.repeat(32))).toBe(403)
	expect(await asked('/zing-local/file/..%5C..%5CWindows%5Cwin.ini')).toBe(403)
	expect(await asked('/zing-local/file/C:/Windows/win.ini')).toBe(403)
	expect(await asked('/zing-local/model/bgmodel', { method: 'POST', body: 'x' })).toBe(403)
	expect(await asked('/zing-local/')).toBe(403)

	// A web page in a tab: the same addresses on the same host name reach nothing, whatever the request mode.
	server = createServer((_, response) => {
		response.writeHead(200, { 'Content-Type': 'text/html' })
		response.end('<title>probe</title>')
	})
	await new Promise<void>((resolve) => server!.listen(0, '127.0.0.1', resolve))
	const origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}/`
	const tab = await zing.chrome.evaluate(async () => (await window.zing.getTabs()).tabs[0]!.id)
	await zing.chrome.evaluate(([id, url]) => window.zing.navigateTab(id as string, url as string), [tab, origin] as const)
	const page = await pageWhere(zing.browser, (p) => p.url() === origin)
	for (const path of ['/zing-local/model/bgmodel', `/zing-local/file/${token}`]) {
		for (const mode of ['cors', 'no-cors'] as const) {
			const outcome = await page.evaluate(([p, m]) => fetch(`https://local.zing${p}`, { mode: m as RequestMode }).then((r) => `answered ${r.status} ${r.type}`, (e: Error) => e.name), [path, mode] as const)
			expect(outcome, `${mode} ${path}`).toBe('TypeError')
		}
	}
	await zing.close()
})

test('a picture that cannot be used is refused plainly before the model is offered', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const { panel } = await openTool(zing)

	const heic = join(zing.userDataDir, 'IMG_0001.heic')
	const header = Buffer.alloc(32)
	header.writeUInt32BE(24, 0)
	header.write('ftypheic', 4, 'ascii')
	writeFileSync(heic, header)
	await choose(zing, panel, heic)
	await panel.getByRole('button', { name: 'Remove background' }).click()
	await expect(panel.getByText(/HEIC or AVIF picture can't be handled by the background remover/)).toBeVisible()
	await expect(panel.getByText(/Use Convert .* or open it in the viewer/)).toBeVisible()

	const huge = join(zing.userDataDir, 'huge.png')
	const png = Buffer.alloc(33)
	Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13]).copy(png, 0)
	png.write('IHDR', 12, 'ascii')
	png.writeUInt32BE(8000, 16)
	png.writeUInt32BE(7000, 20)
	writeFileSync(huge, png)
	await choose(zing, panel, huge)
	await panel.getByRole('button', { name: 'Remove background' }).click()
	await expect(panel.getByText(/8000 by 7000, about 56 megapixels/)).toBeVisible()

	const text = join(zing.userDataDir, 'notes.png')
	writeFileSync(text, 'this is not a picture')
	await choose(zing, panel, text)
	await panel.getByRole('button', { name: 'Remove background' }).click()
	await expect(panel.getByText(/This file can't be handled by the background remover/)).toBeVisible()

	// None of them led to an offer or a download.
	await expect(panel.getByText(/isn't on this PC yet/)).toHaveCount(0)
	expect(fixture.requests.filter((r) => r.includes('isnet'))).toEqual([])
	await zing.close()
})

test('a corrupt picture is a plain error, and Cancel and closing the panel end the worker', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('bgmodel'))
	const { surface, panel } = await openTool(zing)

	// A PNG whose header is fine and whose data is not.
	const corrupt = join(zing.userDataDir, 'broken.png')
	const good = encodePng(new Uint8ClampedArray(32 * 32 * 4).fill(180), 32, 32)
	writeFileSync(corrupt, Buffer.concat([good.subarray(0, 60), Buffer.alloc(40, 7)]))
	await choose(zing, panel, corrupt)
	await panel.getByRole('button', { name: 'Remove background' }).click()
	await expect(panel.getByText(/looks damaged or cut short/)).toBeVisible({ timeout: 30_000 })

	// A picture at the size limit takes long enough to decode to stop it half way.
	const big = join(zing.userDataDir, 'big.png')
	writeFileSync(big, encodePng(new Uint8ClampedArray(6000 * 6000 * 4).fill(200), 6000, 6000))
	await choose(zing, panel, big)
	const clickOn = (label: string): Promise<void> =>
		surface.evaluate((text) => {
			const status = new MutationObserver(() => {
				if (!document.body.textContent?.includes('Decoding the picture')) return
				status.disconnect()
				const target = (text === 'cancel' ? document.querySelector('[aria-label="Cancel background removal"]') : document.querySelector('[aria-label="Close Background Remover"]')) as HTMLElement | null
				target?.click()
			})
			status.observe(document.body, { childList: true, subtree: true, characterData: true })
		}, label)

	await clickOn('cancel')
	await panel.getByRole('button', { name: 'Remove background' }).click()
	await expect(panel.getByText('Cancelled. Nothing was kept.')).toBeVisible({ timeout: 60_000 })
	await expect.poll(() => surface.workers().length).toBe(0)
	expect(savedPngs(zing)).toEqual([])

	await clickOn('close')
	await panel.getByRole('button', { name: 'Remove background' }).click()
	await expect(panel).toHaveCount(0, { timeout: 30_000 })
	await expect.poll(() => surface.workers().length).toBe(0)
	expect(savedPngs(zing)).toEqual([])
	await zing.close()
})

test('a PNG from the tool is saved to Downloads under a numbered name and the original is untouched', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const png = Buffer.from(encodePng(new Uint8ClampedArray(4 * 4 * 4).fill(255), 4, 4))
	const saved = await zing.chrome.evaluate((b64) => Promise.all([window.zing.savePng('cat no background.png', b64), window.zing.savePng('cat no background.png', b64)]), png.toString('base64'))
	expect(saved.map((s) => s.name).sort()).toEqual(['cat no background 2.png', 'cat no background.png'])
	const refused = await zing.chrome.evaluate((b64) => window.zing.savePng('..\\evil.png', b64).then(() => 'saved', (e: Error) => e.name), png.toString('base64'))
	expect(refused).toBe('PngSaveException')
	const notPng = await zing.chrome.evaluate(() => window.zing.savePng('x.png', btoa('MZ not a png')).then(() => 'saved', (e: Error) => e.name))
	expect(notPng).toBe('PngSaveException')
	expect(readdirSync(join(zing.userDataDir, 'downloads')).sort()).toEqual(['cat no background 2.png', 'cat no background.png'])
	await zing.close()
})

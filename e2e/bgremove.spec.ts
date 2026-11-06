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
	// The WebAssembly compiled and ran in the worker, from the app's own bundle. The worker has no policy of its own (the bundle is served
	// without headers and the page's meta policy does not reach a worker); what keeps it local is the guard in the worker and this check.
	expect(requested.some((url) => /ort-wasm-simd-threaded.*\.wasm$/.test(url))).toBe(true)
	// Everything the browser asked for was the app's own bundle or the viewer file server (the picture's session twice, the model once); nothing else.
	expect(requested.filter((url) => !url.startsWith('https://app.zing/') && !url.startsWith('https://view.zing/') && !url.startsWith('data:'))).toEqual([])
	expect(requested.filter((url) => url.startsWith('https://view.zing/')).map((url) => url.replace(/[A-Za-z0-9_-]{32}$/, '<session>'))).toEqual([
		'https://view.zing/<session>',
		'https://view.zing/<session>',
		'https://view.zing/model/bgmodel',
	])
	// And the worker has ended.
	await expect.poll(() => surface.workers().length).toBe(0)
	await zing.close()
})

/** Opens the tool's panel and starts a picture session by the bridge, the way the tool does, and says where to fetch it. */
async function openSession(zing: Launched, file: string): Promise<{ id: string; url: string }> {
	await openTool(zing)
	const panelId = await zing.chrome.evaluate(async () => (await window.zing.getToolPanels()).panels.find((p) => p.content === 'tool:Background Remover')!.id)
	await queuePick(zing, file)
	const token = await zing.chrome.evaluate(async () => (await window.zing.pickFiles(['image']))[0]!.token)
	return zing.chrome.evaluate(([panel, t]) => window.zing.openBackgroundPicture(panel as string, t as string), [panelId, token] as const)
}

test('the viewer file server gives the model and the picture only to a Zing view, only while the picture is open, and takes the result only for that session', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const file = picture(zing, 'tok.png')
	await zing.chrome.evaluate(() => window.zing.ensureEngine('bgmodel'))
	const session = await openSession(zing, file)

	const asked = (path: string, init?: RequestInit): Promise<number | string> =>
		zing.chrome.evaluate(([p, i]) => fetch(p as string, i as RequestInit | undefined).then((r) => r.status, (e: Error) => e.name), [path, init] as const)

	const model = await zing.chrome.evaluate(async () => {
		const response = await fetch('https://view.zing/model/bgmodel')
		return { status: response.status, length: Number(response.headers.get('Content-Length')), bytes: (await response.arrayBuffer()).byteLength, type: response.headers.get('Content-Type') }
	})
	expect(model).toEqual({ status: 200, length: fixture.bytes, bytes: fixture.bytes, type: 'application/octet-stream' })
	const served = await zing.chrome.evaluate(async (url) => Array.from(new Uint8Array(await (await fetch(url)).arrayBuffer())), session.url)
	expect(Buffer.from(served).equals(readFileSync(file))).toBe(true)

	// The model has one id; the address has one shape; a PUT is only for a save.
	expect(await asked('https://view.zing/model/ffmpeg')).toBe('TypeError')
	expect(await asked('https://view.zing/model/ytdlp')).toBe('TypeError')
	expect(await asked('https://view.zing/model/bgmodel/../ffmpeg')).toBe('TypeError')
	expect(await asked('https://view.zing/model/bgmodel?x=1')).toBe('TypeError')
	expect(await asked(`https://view.zing/${'0'.repeat(32)}`)).toBe(404)
	expect(await asked(session.url, { method: 'POST', body: 'x' })).toBe(405)
	expect(await asked(session.url, { method: 'PUT', body: 'x' })).toBe(405)

	// The result: PUT to the session, checked and numbered by the host, off the interface thread.
	const png = Buffer.from(encodePng(new Uint8ClampedArray(4 * 4 * 4).fill(255), 4, 4))
	const put = (name: string, body: BodyInit, id = session.id): Promise<{ status: number; text: string }> =>
		zing.chrome.evaluate(([n, b, i]) => fetch(`https://view.zing/save/${i as string}/${encodeURIComponent(n as string)}`, { method: 'PUT', body: new Uint8Array(b as number[]) }).then(async (r) => ({ status: r.status, text: await r.text() })), [name, Array.from(body as Uint8Array), id] as const)
	const first = await put('cat no background.png', png)
	const second = await put('cat no background.png', png)
	expect([first.status, second.status]).toEqual([201, 201])
	expect([JSON.parse(first.text).name, JSON.parse(second.text).name]).toEqual(['cat no background.png', 'cat no background 2.png'])
	expect(readdirSync(join(zing.userDataDir, 'downloads')).sort()).toEqual(['cat no background 2.png', 'cat no background.png'])
	expect((await put('..\\evil.png', png)).status).toBe(400)
	expect((await put('con.png', png)).status).toBe(201)
	const notPng = await put('x.png', new Uint8Array(Buffer.from('MZ not a png at all')))
	expect(notPng.status).toBe(400)
	expect(notPng.text).toMatch(/not a PNG/)
	expect(await put('y.png', png, 'a'.repeat(32))).toMatchObject({ status: 404 })
	expect(readdirSync(join(zing.userDataDir, 'downloads')).sort()).toEqual(['cat no background 2.png', 'cat no background.png', 'con_.png'])

	// Revoked, nothing is served or saved any more.
	await zing.chrome.evaluate((id) => window.zing.closeViewerFile(id), session.id)
	expect(await asked(session.url)).toBe(404)
	expect(await asked('https://view.zing/model/bgmodel')).toBe(404)
	expect((await put('z.png', png)).status).toBe(404)
	await zing.close()
})

test('a page tab gets nothing from the model, the picture or the save: not by fetch, image, frame, popup or without a referrer', async () => {
	fixture = await startEngineFixture()
	server = createServer((_, response) => {
		response.writeHead(200, { 'Content-Type': 'text/html' })
		response.end('<title>probe</title>')
	})
	await new Promise<void>((resolve) => server!.listen(0, '127.0.0.1', resolve))
	const origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}/`
	const zing = await launchZing({ env: { ...fixture.env, ZING_START_URL: origin } })
	const file = picture(zing, 'tok.png')
	await zing.chrome.evaluate(() => window.zing.ensureEngine('bgmodel'))
	const session = await openSession(zing, file)
	const tab = await pageWhere(zing.browser, (p) => p.url() === origin)
	const model = 'https://view.zing/model/bgmodel'
	const save = `https://view.zing/save/${session.id}/a.png`

	for (const url of [model, session.url, save]) {
		for (const init of [{ mode: 'cors' }, { mode: 'no-cors' }, { mode: 'no-cors', referrerPolicy: 'no-referrer' }, { mode: 'cors', method: 'PUT', body: 'x' }] as const) {
			const outcome = await tab.evaluate(([u, i]) => fetch(u as string, i as RequestInit).then((r) => `answered ${r.status} ${r.type} ${r.headers.get('content-type')}`, (e: Error) => e.name), [url, init] as const)
			expect(outcome, `${init.mode} ${url}`).toBe('TypeError')
		}
	}
	const image = await tab.evaluate((u) => new Promise<string>((resolve) => {
		const img = new Image()
		img.onload = () => resolve('loaded')
		img.onerror = () => resolve('failed')
		img.src = u
	}), session.url)
	expect(image).toBe('failed')
	const noReferrerImage = await tab.evaluate((u) => new Promise<string>((resolve) => {
		const img = new Image()
		img.referrerPolicy = 'no-referrer'
		img.onload = () => resolve('loaded')
		img.onerror = () => resolve('failed')
		img.src = u
	}), session.url)
	expect(noReferrerImage).toBe('failed')
	await tab.evaluate((u) => {
		const frame = document.createElement('iframe')
		frame.src = u
		document.body.append(frame)
	}, session.url)
	await expect.poll(() => tab.frames().some((f) => f.url().startsWith('https://view.zing/') || f.url().startsWith('chrome-error:'))).toBe(true)
	for (const frame of tab.frames().filter((f) => f !== tab.mainFrame())) {
		expect(await frame.evaluate(() => document.contentType).catch(() => 'none')).not.toBe('image/png')
	}
	const popup = tab.waitForEvent('popup', { timeout: 5_000 }).catch(() => null)
	await tab.evaluate((u) => void (globalThis as unknown as { open(address: string): void }).open(u), session.url)
	const opened = await popup
	if (opened) expect(await opened.evaluate(() => document.contentType).catch(() => 'none')).not.toBe('image/png')
	await expect(tab.goto(session.url)).rejects.toThrow()
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
	await expect(panel.getByText(/takes PNG, JPEG and WebP pictures/)).toBeVisible()
	await expect(panel.getByText(/Use Convert to make one first, or open this file in the viewer/)).toBeVisible()

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
	await expect(panel.getByText(/takes PNG, JPEG and WebP pictures/)).toBeVisible()

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

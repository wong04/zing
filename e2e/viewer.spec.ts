import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { startEngineFixture, type EngineFixture } from './engineFixture'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi; readonly screenX: number; readonly screenY: number }

const FIXTURES = join(import.meta.dirname, 'fixtures')
const CLIP = join(FIXTURES, 'clip.mp4')
const FRAGMENTED = join(FIXTURES, 'clip-fragmented.mp4')
const PIXEL = join(FIXTURES, 'pixel.png')

let fixture: EngineFixture | undefined
let work = ''
let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((_req, res) => {
		res.setHeader('content-type', 'text/html')
		res.end('<!doctype html><title>A page tab</title><p>just a page</p>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

test.beforeEach(() => {
	work = mkdtempSync(join(tmpdir(), 'zing-viewer-files-'))
})

test.afterEach(async () => {
	await fixture?.close()
	fixture = undefined
})

function makePdf(pages: number, bytesPerPage: number): Buffer {
	const objects: string[] = []
	const kids: string[] = []
	objects.push('<</Type/Catalog/Pages 2 0 R>>')
	objects.push('')
	const filler = 'BT /F1 18 Tf 72 700 Td (Hello from page PAGE) Tj ET\n' + '0.9 0.9 0.95 rg 72 100 400 30 re f\n'.repeat(Math.ceil(bytesPerPage / 36))
	objects.push('<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>')
	for (let i = 0; i < pages; i++) {
		const content = filler.replace('PAGE', String(i + 1))
		const pageNo = 4 + i * 2
		kids.push(`${pageNo} 0 R`)
		objects.push(`<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents ${pageNo + 1} 0 R>>`)
		objects.push(`<</Length ${content.length}>>\nstream\n${content}endstream`)
	}
	objects[1] = `<</Type/Pages/Kids[${kids.join(' ')}]/Count ${pages}>>`
	let out = '%PDF-1.4\n'
	const offsets: number[] = []
	objects.forEach((o, i) => {
		offsets.push(out.length)
		out += `${i + 1} 0 obj\n${o}\nendobj\n`
	})
	const xref = out.length
	out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n` + offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')
	out += `trailer\n<</Size ${objects.length + 1}/Root 1 0 R>>\nstartxref\n${xref}\n%%EOF\n`
	return Buffer.from(out, 'latin1')
}


/** A copy of a fixture under the name a test wants, in a folder of its own. */
function copyAs(source: string, name: string): string {
	const path = join(work, name)
	copyFileSync(source, path)
	return path
}

async function queuePick(zing: Launched, ...files: string[]): Promise<void> {
	await zing.chrome.evaluate((paths) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: paths }), files)
}

interface Viewer {
	readonly surface: Page
	readonly panel: Locator
}

async function openViewer(zing: Launched): Promise<Viewer> {
	await zing.chrome.evaluate(() => window.zing.openPanel('section:Files', { x: window.screenX + 560, y: window.screenY + 160 }))
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	const panel = surface.getByRole('dialog', { name: 'Files' })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

async function choose(zing: Launched, viewer: Viewer, file: string): Promise<void> {
	await queuePick(zing, file)
	await viewer.panel.getByRole('button', { name: 'Choose a file' }).click()
}

const copies = (zing: Launched): string => join(zing.userDataDir, 'viewer-copies')

function running(pid: number): boolean {
	try {
		process.kill(pid, 0)
		return true
	} catch {
		return false
	}
}

const leftIn = (folder: string): string[] => (existsSync(folder) ? readdirSync(folder) : [])

/** The fake engine stands in for FFmpeg and ImageMagick. A downloaded engine is the one thing a viewer test needs first. */
async function withEngines(): Promise<EngineFixture> {
	fixture = await startEngineFixture()
	return fixture
}

test('a fixture MP4 plays and seeking works', async () => {
	const zing = await launchZing()
	const viewer = await openViewer(zing)
	await choose(zing, viewer, CLIP)
	const video = viewer.panel.getByTestId('viewer-video')
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState)).toBeGreaterThanOrEqual(2)
	const duration = await video.evaluate((v: HTMLVideoElement) => v.duration)
	expect(duration).toBeGreaterThan(5)
	expect(duration).toBeLessThan(7)
	// Ranges are what make this seek: the file is served in pieces from disk.
	await video.evaluate((v: HTMLVideoElement) => {
		v.currentTime = 4
	})
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => !v.seeking && Math.abs(v.currentTime - 4) < 0.3)).toBe(true)
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState)).toBeGreaterThanOrEqual(2)
	await expect(viewer.panel.getByText('Shown from where it is.')).toBeVisible()
	// A file Chromium shows as it is is not copied.
	expect(leftIn(copies(zing))).toEqual([])
	await zing.close()
})

test('a picture and a PDF are shown as they are, from a type chosen from the extension', async () => {
	const zing = await launchZing()
	const viewer = await openViewer(zing)
	await choose(zing, viewer, PIXEL)
	const image = viewer.panel.getByTestId('viewer-image')
	await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.naturalWidth)).toBe(24)
	const url = await image.getAttribute('src')
	expect(url).toMatch(/^https:\/\/view\.zing\/[A-Za-z0-9_-]{32}$/)
	const headers = await viewer.surface.evaluate(async (u) => {
		const response = await fetch(u!)
		return { status: response.status, type: response.headers.get('content-type'), sniff: response.headers.get('x-content-type-options'), policy: response.headers.get('content-security-policy') }
	}, url)
	expect(headers).toEqual({ status: 200, type: 'image/png', sniff: 'nosniff', policy: "sandbox; default-src 'none'; frame-ancestors 'none'" })
	await viewer.panel.getByRole('button', { name: 'Close file', exact: true }).click()

	const pdf = join(work, 'paper.pdf')
	writeFileSync(pdf, '%PDF-1.1\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n')
	await choose(zing, viewer, pdf)
	const frame = viewer.panel.getByTestId('viewer-pdf')
	await expect(frame).toBeVisible()
	const pdfHeaders = await viewer.surface.evaluate(async (u) => {
		const response = await fetch(u!)
		return { type: response.headers.get('content-type'), policy: response.headers.get('content-security-policy') }
	}, await frame.getAttribute('src'))
	expect(pdfHeaders).toEqual({ type: 'application/pdf', policy: "default-src 'none'; frame-ancestors https://app.zing" })
	await zing.close()
})

test('a PDF over the whole-file limit is served without a Range header and in chunks with one', async () => {
	const zing = await launchZing()
	const viewer = await openViewer(zing)
	const pdf = join(work, 'big.pdf')
	writeFileSync(pdf, makePdf(300, 30_000))
	await choose(zing, viewer, pdf)
	const frame = viewer.panel.getByTestId('viewer-pdf')
	await expect(frame).toBeVisible()
	const url = await frame.getAttribute('src')
	const ranged = await viewer.surface.evaluate(async (u) => {
		const response = await fetch(u!, { headers: { Range: 'bytes=0-' } })
		return { status: response.status, bytes: (await response.arrayBuffer()).byteLength, range: response.headers.get('content-range') }
	}, url)
	expect(ranged.status).toBe(206)
	expect(ranged.bytes).toBe(4 * 1024 * 1024)
	expect(ranged.range?.startsWith('bytes 0-4194303/')).toBe(true)
	await zing.close()
})

test('a page named like an image is served as an image that cannot be sniffed into a page', async () => {
	const zing = await launchZing()
	const viewer = await openViewer(zing)
	const evil = join(work, 'evil.png')
	writeFileSync(evil, '<html><script>document.title="pwned"</script>')
	await choose(zing, viewer, evil)
	const url = await viewer.panel.getByTestId('viewer-image').getAttribute('src')
	const reply = await viewer.surface.evaluate(async (u) => {
		const response = await fetch(u!)
		return { type: response.headers.get('content-type'), sniff: response.headers.get('x-content-type-options'), policy: response.headers.get('content-security-policy') }
	}, url)
	expect(reply.type).toBe('image/png')
	expect(reply.sniff).toBe('nosniff')
	expect(reply.policy).toContain('sandbox')
	await zing.close()
})

test('with the fake engine, opening a MOV shows progress and then its video, which plays while it is made', async () => {
	const engines = await withEngines()
	const zing = await launchZing({ env: engines.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	const viewer = await openViewer(zing)
	const mov = copyAs(FRAGMENTED, 'holiday.mov')
	await choose(zing, viewer, mov)
	const video = viewer.panel.getByTestId('viewer-video')
	const stage = viewer.panel.getByTestId('viewer-stage')
	await expect(stage).toBeVisible()
	// Samples taken while it runs: the picture should be ready before the conversion is over.
	await viewer.surface.evaluate(() => {
		const samples: { ready: boolean; converting: boolean }[] = []
		;(window as unknown as { __samples: typeof samples }).__samples = samples
		setInterval(() => {
			const v = document.querySelector<HTMLVideoElement>('[data-testid="viewer-video"]')
			samples.push({ ready: !!v && v.readyState >= 2, converting: !!document.querySelector('[data-testid="viewer-progress"]') })
		}, 50)
	})
	await expect(viewer.panel.getByTestId('viewer-progress')).toBeVisible()
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState), { timeout: 20_000 }).toBeGreaterThanOrEqual(2)
	await expect(viewer.panel.getByTestId('viewer-progress')).toHaveCount(0, { timeout: 20_000 })
	const samples = await viewer.surface.evaluate(() => (window as unknown as { __samples: { ready: boolean; converting: boolean }[] }).__samples)
	expect(samples.some((s) => s.ready && s.converting)).toBe(true)
	// The whole converted file is playable: the bar spans it and a seek lands.
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.duration)).toBeGreaterThan(5)
	await video.evaluate((v: HTMLVideoElement) => {
		v.currentTime = 4.5
	})
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => !v.seeking && Math.abs(v.currentTime - 4.5) < 0.5 && v.readyState >= 2)).toBe(true)
	await video.evaluate((v: HTMLVideoElement) => {
		v.currentTime = 0.5
	})
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => !v.seeking && Math.abs(v.currentTime - 0.5) < 0.5 && v.readyState >= 2)).toBe(true)
	await expect(viewer.panel.getByText('Shown from a temporary copy that is deleted when you close the file.')).toBeVisible()
	await zing.close()
})

test('a seek past what is converted lands on what is ready and does not hang', async () => {
	const engines = await withEngines()
	const zing = await launchZing({ env: engines.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	const viewer = await openViewer(zing)
	await choose(zing, viewer, copyAs(FRAGMENTED, 'holiday.mov'))
	const video = viewer.panel.getByTestId('viewer-video')
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState), { timeout: 20_000 }).toBeGreaterThanOrEqual(2)
	await expect(viewer.panel.getByTestId('viewer-progress')).toBeVisible()
	await video.evaluate((v: HTMLVideoElement) => {
		v.currentTime = 5.9
	})
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => !v.seeking), { timeout: 20_000 }).toBe(true)
	await expect(viewer.panel.getByTestId('viewer-progress')).toHaveCount(0, { timeout: 20_000 })
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState)).toBeGreaterThanOrEqual(2)
	await zing.close()
})

test('a HEIC is converted into a picture', async () => {
	const engines = await withEngines()
	const zing = await launchZing({ env: engines.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('magick'))
	const viewer = await openViewer(zing)
	await choose(zing, viewer, copyAs(PIXEL, 'shot.heic'))
	const image = viewer.panel.getByTestId('viewer-image')
	await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.naturalWidth), { timeout: 20_000 }).toBe(24)
	expect(leftIn(copies(zing))).toHaveLength(1)
	await zing.close()
})

test('closing the file deletes the temporary copy', async () => {
	const engines = await withEngines()
	const zing = await launchZing({ env: engines.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('magick'))
	const viewer = await openViewer(zing)
	await choose(zing, viewer, copyAs(PIXEL, 'shot.heic'))
	await expect(viewer.panel.getByTestId('viewer-image')).toBeVisible()
	const [folder] = leftIn(copies(zing))
	expect(folder).toMatch(/^\d+_/)
	expect(existsSync(join(copies(zing), folder!, 'view.png'))).toBe(true)
	await viewer.panel.getByRole('button', { name: 'Close file', exact: true }).click()
	await expect.poll(() => leftIn(copies(zing))).toEqual([])
	await zing.close()
})

test('closing the panel stops the conversion and deletes the temporary copy', async () => {
	const engines = await withEngines()
	const zing = await launchZing({ env: engines.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	const viewer = await openViewer(zing)
	await choose(zing, viewer, copyAs(FRAGMENTED, 'holiday.mov'))
	await expect(viewer.panel.getByTestId('viewer-progress')).toBeVisible()
	await expect.poll(() => leftIn(copies(zing)).length).toBe(1)
	await viewer.panel.getByRole('button', { name: 'Close Files' }).click()
	await expect(viewer.panel).toHaveCount(0)
	await expect.poll(() => leftIn(copies(zing))).toEqual([])
	// And nothing is placed in Downloads afterwards by a job that was cut short.
	await new Promise((resolve) => setTimeout(resolve, 1500))
	expect(leftIn(copies(zing))).toEqual([])
	expect(leftIn(join(zing.userDataDir, 'downloads'))).toEqual([])
	await zing.close()
})

test('minimizing the panel keeps the file and the copy', async () => {
	const engines = await withEngines()
	const zing = await launchZing({ env: engines.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('magick'))
	const viewer = await openViewer(zing)
	await choose(zing, viewer, copyAs(PIXEL, 'shot.heic'))
	await expect(viewer.panel.getByTestId('viewer-image')).toBeVisible()
	await viewer.panel.getByRole('button', { name: 'Minimize Files to the sidebar' }).click()
	await new Promise((resolve) => setTimeout(resolve, 500))
	expect(leftIn(copies(zing))).toHaveLength(1)
	await zing.close()
})

test('temporary copies left by a crash go at next start', async () => {
	const engines = await withEngines()
	const first = await launchZing({ env: engines.env })
	await first.chrome.evaluate(() => window.zing.ensureEngine('magick'))
	const viewer = await openViewer(first)
	await choose(first, viewer, copyAs(PIXEL, 'shot.heic'))
	await expect(viewer.panel.getByTestId('viewer-image')).toBeVisible()
	const root = copies(first)
	expect(leftIn(root)).toHaveLength(1)
	// A crash: the host is killed, so nothing gets to delete anything.
	process.kill(first.pid)
	await expect.poll(() => running(first.pid)).toBe(false)
	expect(leftIn(root)).toHaveLength(1)
	// A stray from a Zing long dead, as well.
	mkdirSync(join(root, '4000000_old'), { recursive: true })
	writeFileSync(join(root, '4000000_old', 'view.mp4'), 'x')
	const second = await launchZing({ env: engines.env, userDataDir: first.userDataDir })
	expect(leftIn(root)).toEqual([])
	await second.close()
})

test('a token for a closed file answers 404, and so does one that never existed', async () => {
	const zing = await launchZing()
	const viewer = await openViewer(zing)
	await choose(zing, viewer, PIXEL)
	const image = viewer.panel.getByTestId('viewer-image')
	const url = (await image.getAttribute('src'))!
	const status = (u: string): Promise<number> => viewer.surface.evaluate(async (target) => (await fetch(target).catch(() => ({ status: 0 }))).status, u)
	expect(await status(url)).toBe(200)
	await viewer.panel.getByRole('button', { name: 'Close file', exact: true }).click()
	await expect.poll(() => status(url)).toBe(404)
	expect(await status(`https://view.zing/${'a'.repeat(32)}`)).toBe(404)
	// No token at all gets no CORS answer, which a page sees as a failed fetch.
	expect(await status('https://view.zing/')).toBe(0)
	await zing.close()
})

test('a view.zing address from a page tab answers 404, even with a token that is open', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/` } })
	const tab = await pageWhere(zing.browser, (p) => p.url() === `${base}/`)
	const viewer = await openViewer(zing)
	await choose(zing, viewer, PIXEL)
	const url = (await viewer.panel.getByTestId('viewer-image').getAttribute('src'))!
	await expect.poll(() => viewer.surface.evaluate(async (u) => (await fetch(u)).status, url)).toBe(200)

	const loaded = await tab.evaluate(
		(u) =>
			new Promise<string>((resolve) => {
				const image = new Image()
				image.onload = () => resolve('loaded')
				image.onerror = () => resolve('failed')
				image.src = u
			}),
		url,
	)
	expect(loaded).toBe('failed')
	// Asking for the address as a page also gets nothing.
	// An empty 404 is a failed navigation in Chromium: that is the answer.
	await expect(tab.goto(url)).rejects.toThrow(/ERR_HTTP_RESPONSE_CODE_FAILURE/)
	await zing.close()
})

test('a file the viewer cannot show says so and offers Convert', async () => {
	const zing = await launchZing()
	const viewer = await openViewer(zing)
	const model = join(work, 'part.obj')
	writeFileSync(model, 'v 0 0 0\n')
	await choose(zing, viewer, model)
	await expect(viewer.panel.getByTestId('viewer-unsupported')).toHaveText("part.obj is a kind of file the viewer can't show.")
	await viewer.panel.getByRole('button', { name: 'Open Convert' }).click()
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels.map((p) => p.content)).toContain('section:Convert')
	await zing.close()
})

test('a page or an image of vectors is not shown as a page', async () => {
	const zing = await launchZing()
	const viewer = await openViewer(zing)
	for (const name of ['page.html', 'drawing.svg']) {
		const path = join(work, name)
		writeFileSync(path, '<svg xmlns="http://www.w3.org/2000/svg"><script>1</script></svg>')
		await choose(zing, viewer, path)
		await expect(viewer.panel.getByTestId('viewer-unsupported')).toContainText(name)
		await expect(viewer.panel.locator('iframe, img, video, audio')).toHaveCount(0)
	}
	await zing.close()
})

test('a converted file asks for its engine first and nothing downloads until Download is pressed', async () => {
	const engines = await withEngines()
	const zing = await launchZing({ env: engines.env })
	const viewer = await openViewer(zing)
	await choose(zing, viewer, copyAs(FRAGMENTED, 'holiday.mov'))
	await expect(viewer.panel.getByTestId('viewer-needs-engine')).toContainText('FFmpeg')
	expect(engines.requests).toEqual([])
	expect(leftIn(copies(zing))).toEqual([])
	await viewer.panel.getByRole('button', { name: 'Download FFmpeg' }).click()
	await expect(viewer.panel.getByTestId('viewer-video')).toBeVisible({ timeout: 20_000 })
	expect(engines.requests).toEqual(['/ffmpeg.exe'])
	await zing.close()
})

test('a conversion that fails says why and leaves nothing behind', async () => {
	const engines = await withEngines()
	const zing = await launchZing({ env: engines.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	const viewer = await openViewer(zing)
	const empty = join(work, 'broken.mov')
	writeFileSync(empty, '')
	await choose(zing, viewer, empty)
	await expect(viewer.panel.getByRole('alert')).toContainText("Couldn't convert broken.mov")
	await expect(viewer.panel.getByTestId('viewer-stage')).toHaveCount(0)
	await expect.poll(() => leftIn(copies(zing))).toEqual([])
	await zing.close()
})

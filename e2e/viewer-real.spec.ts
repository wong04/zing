// Local test, not in CI: runs the viewer against the real FFmpeg and ImageMagick that Zing downloads from the pinned addresses in
// EngineCatalog.cs. Run by hand with ZING_REAL_ENGINES=1, ZING_REAL_DATA=<a scratch folder outside the repo> and ZING_REAL_MOVIE=<a
// movie in that folder to open>. Nothing leaves this PC but the two engine downloads, and only the first time.
import { existsSync, readdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import type { EngineExit, JobArg } from '../src/shared/engines/engineJob'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi; readonly screenX: number; readonly screenY: number }

const DATA = process.env.ZING_REAL_DATA ?? ''
const MOVIE = process.env.ZING_REAL_MOVIE ?? ''
const PIXEL = join(import.meta.dirname, 'fixtures', 'pixel.png')

test.skip(process.env.ZING_REAL_ENGINES !== '1', 'set ZING_REAL_ENGINES=1 (see the top of this file) to run against the real engines')

async function queuePick(zing: Launched, file: string): Promise<void> {
	await zing.chrome.evaluate((paths) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: paths }), [file])
}

async function openViewer(zing: Launched): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.evaluate(() => window.zing.openPanel('section:Files', { x: window.screenX + 560, y: window.screenY + 160 }))
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	const panel = surface.getByRole('dialog', { name: 'Files' })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

test('the real engines show a HEIC and a HEVC MOV, and a MOV starts playing while it converts', async () => {
	test.setTimeout(600_000)
	expect(DATA && MOVIE, 'ZING_REAL_DATA and ZING_REAL_MOVIE').toBeTruthy()
	const zing = await launchZing({ userDataDir: DATA })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	await zing.chrome.evaluate(() => window.zing.ensureEngine('magick'))

	// A real HEIC, made by the real ImageMagick from the PNG fixture, then opened in the viewer.
	await zing.chrome.evaluate(() => window.zing.openPanel('tool:Hashes', { x: 800, y: 300 }))
	const panel = (await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels[0]!.id
	await queuePick(zing, PIXEL)
	const png = (await zing.chrome.evaluate(() => window.zing.pickFiles(['any'])))[0]!
	const made = await zing.chrome.evaluate(
		([id, token]) =>
			new Promise<EngineExit>((resolve, reject) => {
				let job = ''
				const off = window.zing.onEngineExit((e) => {
					if (e.jobId === job) {
						off()
						resolve(e)
					}
				})
				window.zing.runEngine('magick', id!, { args: [{ input: token! }, '-quality', '80', { output: 0 }] as JobArg[], outputs: ['shot.heic'] }).then((j) => (job = j), reject)
			}),
		[panel, png.token] as const,
	)
	console.log('magick made a HEIC:', made.code, made.error ?? '', made.stderrTail.join(' | '))
	await zing.chrome.evaluate(() => window.zing.closePanel('tool:Hashes'))
	const heic = join(DATA, 'downloads', made.outputs[0]?.name ?? 'missing.heic')
	expect(existsSync(heic), 'ImageMagick wrote a HEIC').toBe(true)

	const viewer = await openViewer(zing)
	await queuePick(zing, heic)
	await viewer.panel.getByRole('button', { name: 'Choose a file' }).click()
	const image = viewer.panel.getByTestId('viewer-image')
	await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.naturalWidth), { timeout: 60_000 }).toBe(24)
	console.log('HEIC shown through the real ImageMagick')
	await viewer.panel.getByRole('button', { name: 'Close file' }).click()

	// The MOV: seconds from the click to the first picture, and to the end of the conversion.
	await viewer.surface.evaluate(() => {
		const stash = window as unknown as { __t: { click?: number; first?: number; done?: number } }
		stash.__t = {}
		document.addEventListener('loadeddata', () => (stash.__t.first ??= performance.now()), true)
		new MutationObserver(() => {
			if (stash.__t.click && !stash.__t.done && document.querySelector('[data-testid="viewer-video"]') && !document.querySelector('[data-testid="viewer-progress"]')) stash.__t.done = performance.now()
		}).observe(document.body, { childList: true, subtree: true })
	})
	await queuePick(zing, MOVIE)
	const choose = viewer.panel.getByRole('button', { name: 'Choose a file' })
	await expect(choose).toBeVisible()
	await choose.evaluate((button) => {
		;(window as unknown as { __t: { click?: number } }).__t.click = performance.now()
		;(button as HTMLButtonElement).click()
	})
	const video = viewer.panel.getByTestId('viewer-video')
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState), { timeout: 120_000 }).toBeGreaterThanOrEqual(2)
	const early = await viewer.surface.evaluate(() => ({ ...(window as unknown as { __t: { click: number; first: number } }).__t, converting: !!document.querySelector('[data-testid="viewer-progress"]') }))
	console.log(`first frame ${((early.first - early.click) / 1000).toFixed(1)} s after the click; conversion still running: ${early.converting}`)

	// It is a real picture, not black: play a moment and read a pixel row.
	await video.evaluate((v: HTMLVideoElement) => v.play())
	await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { timeout: 30_000 }).toBeGreaterThan(1)
	const varied = await video.evaluate((v: HTMLVideoElement) => {
		const canvas = document.createElement('canvas')
		canvas.width = 64
		canvas.height = 36
		const ctx = canvas.getContext('2d')!
		ctx.drawImage(v, 0, 0, 64, 36)
		const data = ctx.getImageData(0, 0, 64, 36).data
		return new Set(Array.from({ length: 64 * 36 }, (_, i) => `${data[i * 4]},${data[i * 4 + 1]},${data[i * 4 + 2]}`)).size
	})
	console.log('distinct colours in the first frame grid:', varied)
	expect(varied).toBeGreaterThan(1)

	await expect(viewer.panel.getByTestId('viewer-progress')).toHaveCount(0, { timeout: 300_000 })
	const timing = await viewer.surface.evaluate(() => (window as unknown as { __t: { click: number; done: number } }).__t)
	console.log(`conversion done ${((timing.done - timing.click) / 1000).toFixed(1)} s after the click; duration ${await video.evaluate((v: HTMLVideoElement) => v.duration)} s`)

	// Seeking in the converted video, far ahead and back.
	const duration = await video.evaluate((v: HTMLVideoElement) => v.duration)
	for (const fraction of [0.8, 0.1, 0.5]) {
		const target = duration * fraction
		await video.evaluate((v: HTMLVideoElement, t) => {
			v.currentTime = t
		}, target)
		await expect.poll(() => video.evaluate((v: HTMLVideoElement) => !v.seeking && v.readyState >= 2), { timeout: 30_000 }).toBe(true)
		const at = await video.evaluate((v: HTMLVideoElement) => v.currentTime)
		console.log(`seek to ${target.toFixed(1)} s landed at ${at.toFixed(1)} s`)
		expect(Math.abs(at - target)).toBeLessThan(3)
	}

	const copies = join(DATA, 'viewer-copies')
	expect(readdirSync(copies)).toHaveLength(1)
	await viewer.panel.getByRole('button', { name: 'Close file' }).click()
	await expect.poll(() => readdirSync(copies)).toEqual([])
	await zing.close()
})

test('a concat file renamed to .mov is refused by the real FFmpeg and its sibling is not read', async () => {
	test.setTimeout(120_000)
	const zing = await launchZing({ userDataDir: DATA })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	const viewer = await openViewer(zing)
	const trap = join(DATA, 'concat-trap.mov')
	writeFileSync(trap, `ffconcat version 1.0\nfile '${MOVIE.split(String.fromCharCode(92)).join('/')}'\n`)
	await queuePick(zing, trap)
	await viewer.panel.getByRole('button', { name: 'Choose a file' }).click()
	await expect(viewer.panel.getByRole('alert')).toContainText("Couldn't convert concat-trap.mov")
	await expect(viewer.panel.getByTestId('viewer-stage')).toHaveCount(0)
	await zing.close()
})

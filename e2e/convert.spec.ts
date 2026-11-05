import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import type { EngineOutput } from '../src/shared/engines/engineJob'
import type { ZingApi } from '../src/shared/ipc'
import { startEngineFixture, type EngineFixture } from './engineFixture'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

// Zing.FakeEngine stands in for ImageMagick, FFmpeg and Pandoc here, so no real engine runs and nothing leaves this PC.
// The real engines are checked by convert-real.spec.ts, by hand.

let fixture: EngineFixture
let server: Server | undefined

test.afterEach(async () => {
	await fixture?.close()
	await new Promise<void>((resolve) => (server ? server.close(() => resolve()) : resolve()))
	server = undefined
})

async function openConvert(zing: Launched): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await surface.getByTestId('toolbox-tile').getByText('Convert', { exact: true }).click()
	const panel = surface.getByRole('dialog', { name: 'Convert', exact: true })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

/** The host's answer to the next file picker, queued first because an e2e run shows no dialog. */
async function queuePick(zing: Launched, ...files: string[]): Promise<void> {
	await zing.chrome.evaluate((paths) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: paths }), files)
}

/** Records every line the engines print, in the chrome view, which is told about every job of the window too. */
async function recordLines(zing: Launched): Promise<void> {
	await zing.chrome.evaluate(() => {
		const stash = globalThis as unknown as { __lines?: string[] }
		stash.__lines = []
		window.zing.onEngineOutput((output: EngineOutput) => stash.__lines!.push(...output.lines.map((l) => l.text)))
	})
}

const linesOf = (zing: Launched): Promise<string[]> => zing.chrome.evaluate(() => (globalThis as unknown as { __lines?: string[] }).__lines ?? [])

async function choose(zing: Launched, panel: Locator, file: string): Promise<void> {
	await queuePick(zing, file)
	await panel.getByRole('button', { name: /^Choose / }).click()
	await expect(panel.getByText(file.split('\\').at(-1)!, { exact: true })).toBeVisible()
}

test('HEIC to JPG: the person is asked before ImageMagick downloads, magick gets the right arguments, and the output is numbered', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await recordLines(zing)
	const input = join(zing.userDataDir, 'IMG_0001.HEIC')
	writeFileSync(input, 'pretend this is a photo')
	const downloads = join(zing.userDataDir, 'downloads')
	// A file with the output's name is already there: it must stay, and the new one must be numbered.
	mkdirSync(downloads, { recursive: true })
	writeFileSync(join(downloads, 'IMG_0001.jpg'), 'mine')

	const { panel } = await openConvert(zing)
	await choose(zing, panel, input)
	await panel.getByRole('button', { name: 'Convert', exact: true }).click()

	// The engine's name, size and source are shown, and nothing was fetched.
	const offer = panel.getByText(/This needs ImageMagick/)
	await expect(offer).toBeVisible()
	await expect(offer).toContainText('KB download')
	await expect(offer).toContainText('/magick.exe')
	expect(fixture.requests).toEqual([])
	expect(existsSync(join(downloads, 'IMG_0001 2.jpg'))).toBe(false)

	await panel.getByRole('button', { name: 'Download ImageMagick' }).click()
	await expect(panel.getByText('Saved to your Downloads folder:')).toBeVisible()
	expect(fixture.requests).toEqual(['/magick.exe'])
	await expect(panel.getByText('IMG_0001 2.jpg', { exact: true })).toBeVisible()

	expect(readFileSync(join(downloads, 'IMG_0001.jpg'), 'utf8')).toBe('mine')
	expect(readFileSync(join(downloads, 'IMG_0001 2.jpg'), 'utf8')).toBe('fake engine output\n')
	expect(readFileSync(input, 'utf8')).toBe('pretend this is a photo')
	// The engine saw the picked file first, the single-frame and rotation settings, and its own output last.
	const echoed = (await linesOf(zing)).map((line) => JSON.parse(line) as string[]).find((args) => Array.isArray(args))!
	expect(echoed.slice(0, 6)).toEqual([input, '-delete', '1--1', '-auto-orient', '-quality', '80'])
	expect(echoed.at(-1)).toMatch(/\.jpg$/)
	expect(echoed).toHaveLength(7)
	await zing.close()
})

test('an engine that fails shows the plain-words reason, and leaves nothing in Downloads', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	const input = join(zing.userDataDir, 'clip.mov')
	writeFileSync(input, 'fake-fail: Invalid data found when processing input\n')

	const { panel } = await openConvert(zing)
	await choose(zing, panel, input)
	await panel.getByRole('button', { name: 'Convert', exact: true }).click()

	await expect(panel.getByRole('alert')).toContainText("the file appears to be corrupt or isn't the format its name claims")
	const downloads = join(zing.userDataDir, 'downloads')
	expect(existsSync(downloads) ? readdirSync(downloads) : []).toEqual([])
	await zing.close()
})

test('Cancel stops a running conversion and keeps no output', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	const input = join(zing.userDataDir, 'long.mov')
	writeFileSync(input, 'fake-sleep: 60000\n')

	const { panel } = await openConvert(zing)
	await choose(zing, panel, input)
	await panel.getByRole('button', { name: 'Convert', exact: true }).click()
	await expect(panel.getByRole('status')).toHaveText('Converting…')
	await panel.getByRole('button', { name: 'Cancel conversion' }).click()

	await expect(panel.getByText('Cancelled. Nothing was kept.')).toBeVisible()
	const downloads = join(zing.userDataDir, 'downloads')
	expect(existsSync(downloads) ? readdirSync(downloads) : []).toEqual([])
	// The tool is usable again.
	await expect(panel.getByRole('button', { name: 'Convert', exact: true })).toBeEnabled()
	await zing.close()
})

test('files the converter does not read are refused with the reason', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const { panel } = await openConvert(zing)

	const svg = join(zing.userDataDir, 'logo.svg')
	writeFileSync(svg, '<svg xmlns="http://www.w3.org/2000/svg"/>')
	await choose(zing, panel, svg)
	await expect(panel.getByRole('alert')).toContainText('SVG')
	await expect(panel.getByRole('button', { name: 'Convert', exact: true })).toHaveCount(0)

	const playlist = join(zing.userDataDir, 'list.m3u8')
	writeFileSync(playlist, '#EXTM3U\nfile:///C:/secret.mp4\n')
	await choose(zing, panel, playlist)
	await expect(panel.getByRole('alert')).toContainText('m3u8')
	await expect(panel.getByRole('button', { name: 'Convert', exact: true })).toHaveCount(0)
	expect(fixture.requests).toEqual([])
	await zing.close()
})

test('HTML to PDF is printed by Chromium with scripts and the network off', async () => {
	fixture = await startEngineFixture()
	// Anything the page tries to reach on the network, or that a script does, shows up here.
	const hits: string[] = []
	server = createServer((req, res) => {
		hits.push(req.url ?? '')
		res.end('x')
	})
	await new Promise<void>((resolve) => server!.listen(0, '127.0.0.1', resolve))
	const origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
	const zing = await launchZing({ env: fixture.env })
	const input = join(zing.userDataDir, 'page.html')
	writeFileSync(
		input,
		`<!doctype html><html><body><h1>Hello PDF</h1>
		<img src="${origin}/image-from-the-network">
		<link rel="preconnect" href="${origin}">
		<script>document.body.append(Object.assign(document.createElement('img'), { src: '${origin}/script-ran' }))</script>
		</body></html>`,
	)

	const { panel } = await openConvert(zing)
	await choose(zing, panel, input)
	await panel.getByRole('button', { name: 'Convert to', exact: true }).click()
	await panel.getByRole('option', { name: 'PDF' }).click()
	await panel.getByRole('button', { name: 'Convert', exact: true }).click()

	await expect(panel.getByText('Saved to your Downloads folder:')).toBeVisible({ timeout: 60_000 })
	await expect(panel.getByText('page.pdf', { exact: true })).toBeVisible()
	const pdf = readFileSync(join(zing.userDataDir, 'downloads', 'page.pdf'))
	expect(pdf.subarray(0, 5).toString('latin1')).toBe('%PDF-')
	expect(pdf.length).toBeGreaterThan(1000)
	expect(readFileSync(input, 'utf8')).toContain('Hello PDF')
	// No fetch, no preconnect and no script ran.
	await new Promise((resolve) => setTimeout(resolve, 1000))
	expect(hits).toEqual([])
	// The throwaway browser profile is gone.
	const scratch = join(zing.userDataDir, 'print-scratch')
	await expect.poll(() => (existsSync(scratch) ? readdirSync(scratch) : [])).toEqual([])
	await zing.close()
})

test('two prints at once both finish: one does not remove the other browser profile', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const files = ['one.html', 'two.html'].map((name) => join(zing.userDataDir, name))
	for (const file of files) writeFileSync(file, `<h1>${file}</h1>`)
	await queuePick(zing, ...files)
	const picked = await zing.chrome.evaluate(() => window.zing.pickFiles(['document']))
	const started = Date.now()
	const results = await zing.chrome.evaluate((tokens) => Promise.all(tokens.map((p, i) => window.zing.printHtmlToPdf(p.token, `out${i}.pdf`))), picked)
	expect(results.map((r) => r.name).sort()).toEqual(['out0.pdf', 'out1.pdf'])
	for (const name of ['out0.pdf', 'out1.pdf']) expect(readFileSync(join(zing.userDataDir, 'downloads', name)).subarray(0, 5).toString('latin1')).toBe('%PDF-')
	// Prints take turns, so a second one is neither concurrent nor delayed by a sweep.
	expect(Date.now() - started).toBeLessThan(60_000)
	await zing.close()
})

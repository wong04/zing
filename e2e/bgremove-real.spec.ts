// The background remover with the REAL model: isnet-general-use.onnx is downloaded from the pinned official address in
// EngineCatalog.cs into this run's scratch profile (outside the repo), then the tool runs it on a picture this test draws.
// Needs the internet and about 180 MB, so it is skipped unless ZING_REAL_ENGINES=1 and is never in CI.
//   ZING_REAL_ENGINES=1 bash <pw.sh> e2e/bgremove-real.spec.ts
// What it shows goes to ZING_REAL_REPORT (a file path) as JSON, so a passing run still gives its seconds and memory.
import { execFile } from 'node:child_process'
import { createHash } from 'node:crypto'
import { appendFileSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect } from '@playwright/test'
import { drawFixture, encodePng } from '../src/shared/convert/bgFixture.node'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const catalog = readFileSync(join(import.meta.dirname, '..', 'host', 'Zing.Core', 'Engines', 'EngineCatalog.cs'), 'utf8')
const pinned = catalog.slice(catalog.indexOf('EngineIds.BackgroundModel,'))
const CATALOG_SHA256 = /"([0-9a-f]{64})"/.exec(pinned)![1]!

/** The working set, in MB, of every WebView2 process of this run (found by its profile folder), summed. */
function webviewMegabytes(userData: string): Promise<number> {
	const script = `(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like '*${userData.replace(/'/g, "''")}*' } | Measure-Object -Property WorkingSetSize -Sum).Sum`
	return new Promise((resolve) => execFile('powershell.exe', ['-NoProfile', '-Command', script], (_, out) => resolve(Math.round(Number(out.trim() || 0) / 1048576))))
}

test.describe('Background remover with the real model', () => {
	test.skip(process.env.ZING_REAL_ENGINES !== '1', 'set ZING_REAL_ENGINES=1 to download and run the real model')
	test.setTimeout(900_000)

	test('the tool cuts a drawn subject out of a plain background, in the worker, from the app bundle', async () => {
		const zing: Launched = await launchZing()
		const installed = await zing.chrome.evaluate(() => window.zing.ensureEngine('bgmodel'))
		expect(installed).toBe('installed')
		const modelPath = join(zing.userDataDir, 'engines', 'bgmodel', 'isnet-general-use.onnx')
		const hash = createHash('sha256').update(readFileSync(modelPath)).digest('hex')
		expect(hash).toBe(CATALOG_SHA256)

		const fixture = drawFixture(800, 600)
		const file = join(zing.userDataDir, 'orange.png')
		writeFileSync(file, encodePng(fixture.rgba, fixture.width, fixture.height))

		await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
		const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
		await surface.getByTestId('toolbox-tile').getByText('Background Remover', { exact: true }).click()
		const panel = surface.getByRole('dialog', { name: 'Background Remover', exact: true })
		await expect(panel).toBeVisible()
		await zing.chrome.evaluate((path) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: [path] }), file)
		await panel.getByRole('button', { name: /^Choose / }).click()
		await expect(panel.getByText('orange.png', { exact: true })).toBeVisible()

		const requests: string[] = []
		for (const context of zing.browser.contexts()) context.on('request', (request) => requests.push(request.url()))
		let peak = await webviewMegabytes(zing.userDataDir)
		const before = peak
		const sampler = setInterval(() => void webviewMegabytes(zing.userDataDir).then((mb) => (peak = Math.max(peak, mb))), 1500)
		const started = Date.now()
		await panel.getByRole('button', { name: 'Remove background' }).click()
		await expect(panel.getByText(/^Saved to your Downloads folder/)).toBeVisible({ timeout: 600_000 })
		const seconds = (Date.now() - started) / 1000
		clearInterval(sampler)

		const saved = readdirSync(join(zing.userDataDir, 'downloads')).filter((name) => name.endsWith('.png'))
		expect(saved).toEqual(['orange no background.png'])

		// The saved PNG is read back in the page, and the see-through channel is scored against what was drawn.
		const output = readFileSync(join(zing.userDataDir, 'downloads', saved[0]!))
		const score = await surface.evaluate(
			async ([b64, truth, width, height]) => {
				const bytes = Uint8Array.from(atob(b64 as string), (c) => c.charCodeAt(0))
				const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }))
				const canvas = new OffscreenCanvas(bitmap.width, bitmap.height)
				const context = canvas.getContext('2d')!
				context.drawImage(bitmap, 0, 0)
				const data = context.getImageData(0, 0, bitmap.width, bitmap.height).data
				const marks = truth as number[]
				let inside = 0
				let outside = 0
				let insideCount = 0
				for (let i = 0; i < marks.length; i++) {
					const alpha = data[i * 4 + 3]! / 255
					if (marks[i]) {
						inside += alpha
						insideCount++
					} else outside += alpha
				}
				return { width: bitmap.width, height: bitmap.height, inside: inside / insideCount, outside: outside / (marks.length - insideCount), expected: [width, height] }
			},
			[output.toString('base64'), Array.from(fixture.truth), fixture.width, fixture.height] as const,
		)
		expect([score.width, score.height]).toEqual([800, 600])
		expect(score.inside).toBeGreaterThan(0.9)
		expect(score.outside).toBeLessThan(0.1)

		// Everything the browser asked for came from the app's own address.
		expect(requests.filter((url) => !url.startsWith('https://app.zing/') && !url.startsWith('https://local.zing/') && !url.startsWith('data:'))).toEqual([])
		expect(requests.some((url) => /ort-wasm-simd-threaded.*\.wasm$/.test(url))).toBe(true)
		await expect.poll(() => surface.workers().length).toBe(0)

		const report = { sha256: hash, catalogSha256: CATALOG_SHA256, seconds, webviewMegabytesBefore: before, webviewMegabytesPeak: peak, insideAlpha: score.inside, outsideAlpha: score.outside }
		if (process.env.ZING_REAL_REPORT) appendFileSync(process.env.ZING_REAL_REPORT, JSON.stringify(report) + '\n')
		process.stdout.write(`bgremove real: ${JSON.stringify(report)}\n`)
		await zing.close()
	})
})

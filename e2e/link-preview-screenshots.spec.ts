// An open link preview, light and dark, into docs/screenshots/link-preview. The preview is a view of its own, so the
// whole window is drawn from the screen (window-shot.ps1), not through the chrome page's DevTools.
// Skipped unless ZING_SCREENSHOTS=1: run by hand, as the window opens on screen (ZING_E2E_ONSCREEN=1, like the real-input tier) for a few seconds.
import { execFileSync } from 'node:child_process'
import { mkdirSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { viewAccent } from './theme-helpers'
import { launchZing, layers, pageWhere, peekState, test } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots', 'link-preview')
const SCRIPT = join(import.meta.dirname, 'window-shot.ps1')
const MODES = ['light', 'dark'] as const

let server: Server
let own = ''
let other = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const host = req.headers.host ?? ''
		const port = host.slice(host.lastIndexOf(':') + 1)
		const elsewhere = host.startsWith('127.0.0.1') ? `http://localhost:${port}` : `http://127.0.0.1:${port}`
		res.setHeader('content-type', 'text/html')
		if (req.url === '/article')
			return void res.end(`<!doctype html><title>Article</title><body style="font:16px system-ui;margin:0;padding:48px;background:#fff;color:#222">
<h1>A page opened as a preview</h1><p>It sits over the tab it came from, with the tab dimmed behind it.</p></body>`)
		res.end(`<!doctype html><title>Source</title><body style="font:16px system-ui;margin:0;padding:48px;background:#fff;color:#222">
<h1>The tab you were reading</h1><p>A link to <a id="cross" href="${elsewhere}/article">another site</a> opens over this page.</p>
<p>${'Lorem ipsum dolor sit amet. '.repeat(40)}</p></body>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	const port = (server.address() as AddressInfo).port
	own = `http://127.0.0.1:${port}`
	other = `http://localhost:${port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

test.describe('Link preview screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the link preview pictures')

	for (const mode of MODES) {
		test(`an open link preview, ${mode}`, async () => {
			mkdirSync(OUT_DIR, { recursive: true })
			const zing = await launchZing({ env: { ZING_START_URL: `${own}/`, ZING_E2E_ONSCREEN: '1' } })
			const { chrome } = zing
			const source = await pageWhere(zing.browser, (p) => p.url() === `${own}/`)
			await source.waitForLoadState('load')
			await chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(chrome)).dark).toBe(mode === 'dark')
			await source.click('#cross')
			await expect.poll(async () => (await peekState(chrome)).layers.map((l) => l.url)).toEqual([`${other}/article`])
			const page = await pageWhere(zing.browser, (p) => p.url() === `${other}/article`)
			await page.waitForLoadState('load')
			// Scrim, preview page and buttons all showing, then the entrance animation done.
			await expect.poll(async () => {
				const shown = (await layers(chrome)).filter((l) => l.visible)
				return shown.some((l) => l.url === 'scrim') && shown.some((l) => l.url === `${other}/article`) && shown.some((l) => l.url.endsWith('#peek'))
			}, { timeout: 45_000 }).toBe(true)
			await chrome.waitForTimeout(800)
			execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', SCRIPT, '-ProcessId', String(zing.pid), '-Out', join(OUT_DIR, `link-preview-${mode}.png`)], { timeout: 60_000 })
			await zing.close()
		})
	}
})

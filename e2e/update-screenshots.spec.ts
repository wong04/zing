// Phase 8 task 6: the "Relaunch to update" pill and Settings → About, light and dark, into docs/screenshots/phase-8.
// Skipped unless ZING_SCREENSHOTS=1, as it is run by hand.
import { execFileSync } from 'node:child_process'
import { mkdirSync } from 'node:fs'
import { join } from 'node:path'
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { viewAccent } from './theme-helpers'
import { launchZing, layers, pageWhere, test } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots', 'phase-8')
const MODES = ['light', 'dark'] as const
const SCRIPT = join(import.meta.dirname, 'window-shot.ps1')

test.describe('Phase 8 update screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the Phase 8 pictures')

	for (const mode of MODES) {
		test(`the update pill and About, ${mode}`, async () => {
			test.setTimeout(120_000)
			mkdirSync(OUT_DIR, { recursive: true })
			const zing = await launchZing()
			await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(zing.chrome)).dark).toBe(mode === 'dark')
			await zing.chrome.evaluate(() =>
				(globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'update-ready', version: '0.9.1' }),
			)
			await zing.chrome.evaluate(() => window.zing.openSettings('about'))
			const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
			// Playwright pins every page it attaches to at light, over the host's override, and clearing that drops both.
			await sheet.emulateMedia({ colorScheme: mode })
			await expect.poll(async () => (await viewAccent(sheet)).dark).toBe(mode === 'dark')
			await sheet.getByRole('button', { name: 'Check now' }).click()
			await expect(sheet.getByText('Ready: relaunch to update')).toBeVisible()
			await sheet.waitForTimeout(400)
			await sheet.getByRole('dialog', { name: 'Settings' }).screenshot({ path: join(OUT_DIR, `about-${mode}.png`) })

			const pill = await pageWhere(zing.browser, (p) => p.url().endsWith('#update-pill'))
			await pill.emulateMedia({ colorScheme: mode })
			await expect.poll(async () => (await viewAccent(pill)).dark).toBe(mode === 'dark')
			await expect(pill.getByRole('button', { name: 'Relaunch to update' })).toBeVisible()
			await pill.waitForTimeout(400)
			await pill.locator('.update-pill').screenshot({ path: join(OUT_DIR, `update-pill-${mode}.png`), omitBackground: true })
			await zing.close()
		})

		// The whole window, drawn from the screen (window-shot.ps1): it opens on screen for a few seconds (ZING_E2E_ONSCREEN=1).
		test(`the update pill in the window's corner, ${mode}`, async () => {
			test.setTimeout(120_000)
			mkdirSync(OUT_DIR, { recursive: true })
			const zing = await launchZing({ env: { ZING_E2E_ONSCREEN: '1' } })
			await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(zing.chrome)).dark).toBe(mode === 'dark')
			await zing.chrome.evaluate(() =>
				(globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'update-ready', version: '0.9.1' }),
			)
			await zing.chrome.evaluate(() => window.zing.checkForUpdates())
			await expect.poll(async () => (await layers(zing.chrome)).some((l) => l.url.endsWith('#update-pill') && l.visible)).toBe(true)
			await zing.chrome.waitForTimeout(800)
			execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', SCRIPT, '-ProcessId', String(zing.pid), '-Out', join(OUT_DIR, `update-pill-window-${mode}.png`)], { timeout: 60_000 })
			await zing.close()
		})
	}
})

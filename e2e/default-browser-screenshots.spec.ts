// Phase 8 task 7: the default-browser offer and Settings' Default browser row, light and dark, into docs/screenshots/phase-8.
// Skipped unless ZING_SCREENSHOTS=1, as it is run by hand.
import { mkdirSync } from 'node:fs'
import { join } from 'node:path'
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { viewAccent } from './theme-helpers'
import { launchZing, pageWhere, test } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots', 'phase-8')
const MODES = ['light', 'dark'] as const

test.describe('Phase 8 default-browser screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the Phase 8 pictures')

	for (const mode of MODES) {
		test(`the default-browser offer and row, ${mode}`, async () => {
			test.setTimeout(180_000)
			mkdirSync(OUT_DIR, { recursive: true })
			// The offer never comes on a profile's first launch, so this one is the second.
			const first = await launchZing()
			await expect.poll(() => first.chrome.evaluate(async () => (await window.zing.getSettings()).launchedBefore)).toBe(true)
			await first.close()

			const zing = await launchZing({ userDataDir: first.userDataDir })
			await expect.poll(() => zing.chrome.evaluate(async () => (await window.zing.getSettings()).launchedBefore)).toBe(true)
			await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(zing.chrome)).dark).toBe(mode === 'dark')
			await zing.chrome.evaluate(() =>
				(globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'show-default-browser-offer' }),
			)
			const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
			// Playwright pins every page it attaches to at light, over the host's override, and clearing that drops both.
			await overlay.emulateMedia({ colorScheme: mode })
			await expect.poll(async () => (await viewAccent(overlay)).dark).toBe(mode === 'dark')
			const offer = overlay.getByTestId('default-browser-offer')
			await expect(offer).toContainText('Make Zing your default browser?')
			await overlay.waitForTimeout(400)
			await offer.screenshot({ path: join(OUT_DIR, `default-browser-offer-${mode}.png`), omitBackground: true })

			await zing.chrome.evaluate(() => window.zing.openSettings('general'))
			const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
			await sheet.emulateMedia({ colorScheme: mode })
			await expect.poll(async () => (await viewAccent(sheet)).dark).toBe(mode === 'dark')
			const row = sheet.locator('.setting-row', { hasText: 'Default browser' })
			await expect(row).toContainText('Install Zing to set it as default')
			await sheet.waitForTimeout(400)
			await row.screenshot({ path: join(OUT_DIR, `default-browser-row-${mode}.png`) })
			await zing.close()
		})
	}
})

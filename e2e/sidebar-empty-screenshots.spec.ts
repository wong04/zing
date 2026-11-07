// The sidebar with nothing saved in it, light and dark, into docs/screenshots/sidebar-empty.
// Skipped unless ZING_SCREENSHOTS=1, as it is run by hand.
import { mkdirSync } from 'node:fs'
import { join } from 'node:path'
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { viewAccent } from './theme-helpers'
import { launchZing, test } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots', 'sidebar-empty')
const MODES = ['light', 'dark'] as const

test.describe('Empty sidebar screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the empty sidebar pictures')

	for (const mode of MODES) {
		test(`the sidebar with no favourites or pinned items, ${mode}`, async () => {
			mkdirSync(OUT_DIR, { recursive: true })
			const zing = await launchZing()
			const { chrome } = zing
			await chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'left'))
			await chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(chrome)).dark).toBe(mode === 'dark')
			const favourites = chrome.getByRole('list', { name: 'Favourites' })
			await expect(favourites).toBeVisible()
			await expect(chrome.getByRole('region', { name: 'Pinned' })).toBeVisible()
			await chrome.waitForTimeout(400)
			const rail = (await favourites.evaluate((el) => {
				const box = el.closest('.rail')!.getBoundingClientRect()
				return { x: box.x, y: box.y, width: box.width, height: Math.min(box.height, 360) }
			}))
			await chrome.screenshot({ path: join(OUT_DIR, `sidebar-empty-${mode}.png`), clip: rail })
			await zing.close()
		})
	}
})

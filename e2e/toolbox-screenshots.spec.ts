// Phase 6 PR A: one picture of the sheet and each tool, light and dark, into docs/screenshots/phase-6.
// Skipped unless ZING_SCREENSHOTS=1, as it is run by hand.
import { mkdirSync } from 'node:fs'
import { join } from 'node:path'
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { itemsIn, toolboxGroups } from '../src/shared/toolbox/catalog'
import { panelId } from '../src/shared/toolbox/panelContent'
import { viewAccent } from './theme-helpers'
import { launchZing, pageWhere, test } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots', 'phase-6')
const MODES = ['light', 'dark'] as const
const LATER_PHASE: readonly string[] = [
	'section:Convert',
	'section:Download',
	'section:Files',
	'tool:Background Remover',
]
const TOOL_IDS = toolboxGroups.flatMap((group) => itemsIn(group).map(panelId)).filter((id) => !LATER_PHASE.includes(id))

const fileName = (id: string, mode: string): string => `${id.replace(/^\w+:/, '').replace(/\W+/g, '-').toLowerCase()}-${mode}.png`

test.describe('Phase 6 screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the Phase 6 pictures')

	for (const mode of MODES) {
		test(`the sheet and every tool, ${mode}`, async () => {
			test.setTimeout(300_000)
			mkdirSync(OUT_DIR, { recursive: true })
			const zing = await launchZing()
			const { chrome } = zing
			await chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(chrome)).dark).toBe(mode === 'dark')

			await chrome.getByRole('button', { name: 'Toolbox' }).first().click()
			const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
			await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
			// Playwright pins every page it attaches to at light, over the host's override, and clearing that drops both.
			await surface.emulateMedia({ colorScheme: mode })
			await expect.poll(async () => (await viewAccent(surface)).dark).toBe(mode === 'dark')
			await surface.waitForTimeout(400)
			await surface.screenshot({ path: join(OUT_DIR, `toolbox-${mode}.png`) })
			await surface.keyboard.press('Escape')
			await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toHaveCount(0)

			for (const id of TOOL_IDS) {
				await chrome.evaluate((content) => window.zing.openPanel(content, { x: 300, y: 80 }), id)
				const panel = surface.getByTestId('float-panel')
				await expect(panel).toHaveCount(1)
				await expect(panel.locator('.float-body > *').first()).toBeVisible()
				await surface.waitForTimeout(400)
				await panel.screenshot({ path: join(OUT_DIR, fileName(id, mode)) })
				await chrome.evaluate(async () => {
					const { panels } = await window.zing.getToolPanels()
					for (const open of panels) await window.zing.closePanel(open.id)
				})
				await expect(panel).toHaveCount(0)
			}
			await zing.close()
		})
	}
})

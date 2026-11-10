import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, scratchDir, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

async function surfaceOf(zing: Launched): Promise<Page> {
	return pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
}

/** Opens the Toolbox, picks a tile, and returns the floating panel the tool lives in. */
async function openTool(zing: Launched, title: string): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await surfaceOf(zing)
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').filter({ hasText: title }).click()
	const panel = surface.getByRole('dialog', { name: title })
	await expect(panel).toBeVisible()
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toHaveCount(0)
	return { surface, panel }
}

/** Opens a second tool without the sheet, which a panel already open may cover. */
async function openToolDirect(zing: Launched, surface: Page, id: string, title: string, at: number): Promise<Locator> {
	await zing.chrome.evaluate(([panel, x]) => window.zing.openPanel(panel!, { x: Number(x), y: 300 }), [id, String(at)])
	const panel = surface.getByRole('dialog', { name: title })
	await expect(panel).toBeVisible()
	return panel
}

test('clicking a field in a background panel keeps its focus, scroll and node', async () => {
	const zing = await launchZing()
	const back = await openTool(zing, 'Hashes')
	const front = await openToolDirect(zing, back.surface, 'tool:HMAC', 'HMAC', 1500)
	await expect(front).toHaveClass(/front/)
	// A panel focuses its first field as it opens; wait for that, so it cannot land in the middle of the check.
	await expect(front.getByRole('textbox', { name: 'Secret key' })).toBeFocused()
	const field = back.panel.getByRole('textbox', { name: 'Text to hash' })
	await field.fill(Array.from({ length: 80 }, (_, i) => `line ${i}`).join('\n'))
	// React restores focus after it moves a node, so focus alone survives the bug: the node being re-inserted shows
	// as a blur event and a scroll position reset.
	await field.evaluate((node) => {
		const box = node as HTMLTextAreaElement
		box.scrollTop = 200
		Object.assign(box, { marker: 'same node', blurs: 0 })
		box.addEventListener('blur', () => ((box as unknown as { blurs: number }).blurs += 1))
	})
	await field.dispatchEvent('pointerdown')
	await expect(back.panel).toHaveClass(/front/)
	const after = await field.evaluate((node) => {
		const box = node as HTMLTextAreaElement & { marker?: string; blurs: number }
		return { blurs: box.blurs, scrollTop: box.scrollTop, kept: box.marker === 'same node' }
	})
	expect(after).toEqual({ blurs: 0, scrollTop: 200, kept: true })
	await zing.close()
})

test('a panel opened behind the Toolbox sheet is covered by it', async () => {
	const zing = await launchZing()
	const { surface, panel } = await openTool(zing, 'Hashes')
	await openToolDirect(zing, surface, 'tool:HMAC', 'HMAC', 1500)
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	const box = await panel.boundingBox()
	if (!box) throw new Error('the panel has no box')
	const covered = await surface.evaluate(
		([x, y]) => document.elementFromPoint(x!, y!)?.closest('.sheet-backdrop') !== null,
		[box.x + box.width / 2, box.y + box.height / 2],
	)
	expect(covered).toBe(true)
	await zing.close()
})

test('a tool that throws while rendering shows its message, and the rest of the surface survives', async () => {
	const zing = await launchZing()
	const { surface, panel } = await openTool(zing, 'Timestamps')
	const other = await openToolDirect(zing, surface, 'tool:HMAC', 'HMAC', 1500)
	// Real input, real render path: the tool formats its dates with toISOString on every render.
	await surface.evaluate(() => {
		Date.prototype.toISOString = () => {
			throw new Error('Patched toISOString failure')
		}
	})
	await panel.getByRole('textbox', { name: 'Timestamp' }).fill('1753800000')
	await expect(panel.getByRole('alert')).toContainText('Patched toISOString failure')
	await expect(other.getByRole('textbox', { name: 'Secret key' })).toBeVisible()
	await panel.getByRole('button', { name: 'Close Timestamps' }).click()
	await expect(panel).toHaveCount(0)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels.map((p) => p.content)).toEqual(['tool:HMAC'])
	// The user sees a message, but it is still a bug, so the host records it and an unplanned one fails the run.
	await expect(zing.close()).rejects.toThrow(/renderer: .*Patched toISOString failure/)
})

test('three panels show at once, a fourth is refused, and one minimised to the sidebar comes back', async () => {
	// The app sidebar, with its Minimized list, sits beside the page when the tabs are on top. Set before launch: changing
	// it later rebuilds the views.
	const profile = scratchDir('zing-host-data-')
	writeFileSync(join(profile, 'settings.json'), JSON.stringify({ tabBarPosition: 'top' }))
	const zing = await launchZing({ userDataDir: profile })
	const open = (content: string, x: number) => zing.chrome.evaluate(([panel, at]) => window.zing.openPanel(panel!, { x: Number(at), y: 300 }), [content, String(x)])
	const shown = async () => (await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels.filter((p) => !p.minimized).map((p) => p.content).sort()

	await expect(zing.chrome.getByRole('navigation', { name: 'App sidebar' })).toBeVisible()
	expect(await open('tool:Hashes', 500)).toEqual({ status: 'opened' })
	const surface = await surfaceOf(zing)
	const hashes = surface.getByRole('dialog', { name: 'Hashes' })
	await expect(hashes).toBeVisible()
	expect(await open('tool:HMAC', 900)).toEqual({ status: 'opened' })
	expect(await open('tool:JSON', 1400)).toEqual({ status: 'opened' })
	expect(await open('tool:UUID', 600)).toEqual({ status: 'full' })
	expect(await shown()).toEqual(['tool:HMAC', 'tool:Hashes', 'tool:JSON'])

	// Where panels land depends on the window's size, and on a smaller one another panel can cover this button.
	await hashes.getByRole('button', { name: 'Minimize Hashes to the sidebar' }).dispatchEvent('click')
	await expect(hashes).toBeHidden()
	const row = zing.chrome.getByTestId('minimized-row').filter({ hasText: 'Hashes' })
	await expect(row).toBeVisible()

	// Minimising freed a slot, so the fourth opens, and now the restore is refused instead.
	expect(await open('tool:UUID', 600)).toEqual({ status: 'opened' })
	const idOf = async (content: string) => (await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels.find((p) => p.content === content)!.id
	expect(await zing.chrome.evaluate((panel) => window.zing.restorePanel(panel), await idOf('tool:Hashes'))).toEqual({ status: 'full' })
	await expect(hashes).toBeHidden()

	await zing.chrome.evaluate((panel) => window.zing.closePanel(panel), await idOf('tool:UUID'))
	await expect(surface.getByRole('dialog', { name: 'UUID' })).toHaveCount(0)
	await row.click()
	await expect(hashes).toBeVisible()
	await expect(row).toHaveCount(0)
	expect(await shown()).toEqual(['tool:HMAC', 'tool:Hashes', 'tool:JSON'])
	await zing.close()
})

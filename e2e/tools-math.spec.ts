import { expect, type Locator, type Page } from '@playwright/test'
import { launchZing, pageWhere, test, type Launched } from './zing'

const isSurface = (url: string) => url.endsWith('#surface')

/** Opens the Toolbox, picks the tile, and returns the tool's floating panel. */
async function openTool(zing: Launched, name: string): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => isSurface(p.url()))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').filter({ hasText: name }).click()
	const panel = surface.getByRole('dialog', { name })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

test('the calculator evaluates 2+2*3', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Scientific Calculator')
	await panel.getByRole('textbox', { name: 'Expression' }).fill('2+2*3')
	await expect(panel.getByTestId('calc-result')).toHaveText('8')
	await zing.close()
})

test('an unbalanced expression shows its error and no result', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Scientific Calculator')
	await panel.getByRole('textbox', { name: 'Expression' }).fill('sin(')
	await expect(panel.getByTestId('calc-error')).toHaveText('Unbalanced parentheses')
	await expect(panel.getByTestId('calc-result')).toHaveCount(0)
	await zing.close()
})

/** Opens `menu` and returns the list. */
async function choose(surface: Page, panel: Locator, menu: string, option: string): Promise<void> {
	await panel.getByRole('button', { name: menu }).click()
	await surface.getByRole('option', { name: option, exact: true }).click()
}

test('the unit converter turns 1 mi into km', async () => {
	const zing = await launchZing()
	const { surface, panel } = await openTool(zing, 'Unit Converter')
	await panel.getByRole('textbox', { name: 'Amount' }).fill('1')
	await choose(surface, panel, 'From unit', 'mi')
	await choose(surface, panel, 'To unit', 'km')
	await expect(panel.getByTestId('unit-result')).toContainText('1.60934 km')
	await zing.close()
})

test('the number base tool shows 255 as ff', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Number Bases')
	await panel.getByRole('textbox', { name: 'Number' }).fill('255')
	await expect(panel.getByTestId('base-Hexadecimal')).toContainText(/0xff/i)
	await zing.close()
})

/** Counts the canvas pixels close to the first series colour (199, 84, 61): the curve, not the grid. */
function curveInk(canvas: Locator): Promise<number> {
	return canvas.evaluate((el) => {
		const c = el as HTMLCanvasElement
		const { data } = c.getContext('2d')!.getImageData(0, 0, c.width, c.height)
		let count = 0
		for (let i = 0; i < data.length; i += 4) {
			if (Math.abs(data[i]! - 199) < 12 && Math.abs(data[i + 1]! - 84) < 12 && Math.abs(data[i + 2]! - 61) < 12) count++
		}
		return count
	})
}

test('the graph draws x^2 and a wheel zoom changes the viewport', async () => {
	const zing = await launchZing()
	const { surface, panel } = await openTool(zing, 'Graphing Calculator')
	const first = panel.getByRole('textbox', { name: 'Expression 1' })
	await first.fill('x^2')
	await panel.getByRole('button', { name: 'Remove expression 2' }).click()
	const canvas = panel.getByTestId('graph-canvas')
	await expect.poll(() => curveInk(canvas)).toBeGreaterThan(200)

	const label = panel.getByTestId('graph-viewport')
	const before = await label.textContent()
	const box = await canvas.boundingBox()
	if (!box) throw new Error('graph canvas has no box')
	await surface.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
	await surface.mouse.wheel(0, -300)
	await expect(label).not.toHaveText(before ?? '')
	await zing.close()
})

test('a unit menu stays inside a small window, and a non-finite amount is refused', async () => {
	const zing = await launchZing()
	const { surface, panel } = await openTool(zing, 'Unit Converter')
	await surface.setViewportSize({ width: 700, height: 420 })
	await panel.getByRole('button', { name: 'From unit' }).click()
	const list = surface.getByRole('listbox', { name: 'From unit' })
	await expect(list).toBeVisible()
	const box = await list.boundingBox()
	const view = surface.viewportSize()
	if (!box || !view) throw new Error('no geometry for the list')
	expect(box.y).toBeGreaterThanOrEqual(0)
	expect(box.y + box.height).toBeLessThanOrEqual(view.height)
	await surface.keyboard.press('Escape')
	await panel.getByRole('textbox', { name: 'Amount' }).fill('1e999')
	await expect(panel.getByText('Enter a number to convert.')).toBeVisible()
	await expect(panel.getByTestId('unit-result')).toHaveCount(0)
	await zing.close()
})

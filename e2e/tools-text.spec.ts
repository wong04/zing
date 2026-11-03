import { expect, type Locator, type Page } from '@playwright/test'
import { launchZing, pageWhere, test, type Launched } from './zing'

async function openTool(zing: Launched, title: string): Promise<Locator> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await surface.getByTestId('toolbox-tile').filter({ hasText: title }).click()
	const panel = surface.getByRole('dialog', { name: title, exact: true })
	await expect(panel).toBeVisible()
	return panel
}

interface Pasted {
	readonly text: string
	readonly fileTypes: readonly string[]
}

// Presses Ctrl+V in a throwaway editable element and reports what the page was handed.
async function paste(page: Page): Promise<Pasted> {
	await page.evaluate(() => {
		const field = document.createElement('div')
		field.contentEditable = 'true'
		document.body.append(field)
		field.focus()
		const w = window as unknown as { pasted: Promise<Pasted> }
		w.pasted = new Promise((resolve) =>
			field.addEventListener(
				'paste',
				(e) => resolve({ text: e.clipboardData?.getData('text/plain') ?? '', fileTypes: Array.from(e.clipboardData?.files ?? [], (f) => f.type) }),
				{ once: true },
			),
		)
	})
	await page.keyboard.press('Control+V')
	return page.evaluate(() => (window as unknown as { pasted: Promise<Pasted> }).pasted)
}

test('Text Case converts Hello World to snake_case', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'Text Case')
	await panel.getByRole('textbox', { name: 'Text' }).fill('Hello World')
	await expect(panel.getByTestId('case-snake_case')).toHaveText('hello_world')
	await zing.close()
})

test('Text Stats counts graphemes, not UTF-16 units', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'Text Stats')
	await panel.getByRole('textbox', { name: 'Text' }).fill('a👨‍👩‍👧b')
	await expect(panel.getByTestId('stat-characters')).toHaveText('3')
	await expect(panel.getByTestId('stat-words')).toHaveText('1')
	await zing.close()
})

test('Color Converter shows rgb for a hex colour and rejects nonsense', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'Color Converter')
	const field = panel.getByRole('textbox', { name: 'Colour' })
	await field.fill('#ff0000')
	await expect(panel.getByTestId('color-rgb')).toHaveText('rgb(255, 0, 0)')
	await field.fill('not a colour')
	await expect(panel.getByRole('alert')).toBeVisible()
	await zing.close()
})

test('QR Code draws a canvas for the text', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'QR Code')
	await expect(panel.locator('canvas')).toHaveCount(0)
	await panel.getByRole('textbox', { name: 'Text' }).fill('https://example.com')
	const canvas = panel.locator('canvas')
	await expect(canvas).toBeVisible()
	// A real code has both dark and light modules.
	const dark = await canvas.evaluate((el: HTMLCanvasElement) => {
		const data = el.getContext('2d')?.getImageData(0, 0, el.width, el.height).data
		if (!data) throw new Error('canvas has no 2d context')
		let count = 0
		for (let i = 0; i < data.length; i += 4) if ((data[i] ?? 255) < 128) count++
		return count
	})
	expect(dark).toBeGreaterThan(50)
	await zing.close()
})

test('Passwords are the chosen length and differ between clicks', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'Passwords')
	await panel.getByRole('slider', { name: 'Length' }).focus()
	await panel.page().keyboard.press('Home')
	await expect(panel.getByRole('slider', { name: 'Length' })).toHaveAttribute('aria-valuenow', '8')
	const rows = panel.getByTestId('password')
	await panel.getByRole('button', { name: 'Generate 5' }).click()
	await expect(rows).toHaveCount(5)
	const first = await rows.allTextContents()
	for (const p of first) expect(p).toHaveLength(8)
	await panel.getByRole('button', { name: 'Generate 5' }).click()
	const second = await rows.allTextContents()
	expect(second).not.toEqual(first)
	await zing.close()
})

test('Link Cleaner strips trackers and names them', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'Link Cleaner')
	const field = panel.getByRole('textbox', { name: 'Link' })
	await field.fill('https://a.com/?utm_source=chatgpt.com&x=1')
	await expect(panel.getByTestId('cleaned-link')).toHaveText('https://a.com/?x=1')
	await expect(panel.getByText('utm_source')).toBeVisible()
	await field.fill('')
	await field.fill('http://')
	await expect(panel.getByRole('alert')).toContainText('not a link')
	await zing.close()
})

test('Passwords honour a length above the minimum', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'Passwords')
	await panel.getByRole('slider', { name: 'Length' }).focus()
	await panel.page().keyboard.press('Home')
	await panel.page().keyboard.press('ArrowRight')
	await panel.page().keyboard.press('ArrowRight')
	await panel.page().keyboard.press('ArrowRight')
	await panel.getByRole('button', { name: 'Generate 5' }).click()
	const rows = panel.getByTestId('password')
	await expect(rows).toHaveCount(5)
	for (const p of await rows.allTextContents()) expect(p).toHaveLength(11)
	await zing.close()
})

test('a password Copy button puts that password on the clipboard', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'Passwords')
	await panel.getByRole('button', { name: 'Generate 5' }).click()
	const second = panel.getByTestId('password').nth(1)
	const password = await second.innerText()
	await panel.getByRole('button', { name: 'Copy password' }).nth(1).click()
	await expect(panel.getByRole('button', { name: 'Copied' })).toBeVisible()
	// Reading through navigator.clipboard waits on a permission prompt, so paste into a field the way a person would.
	expect((await paste(panel.page())).text).toBe(password)
	await zing.close()
})

test('QR Copy image puts a PNG on the clipboard', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'QR Code')
	await panel.getByRole('textbox', { name: 'Text' }).fill('https://example.com')
	await panel.getByRole('button', { name: 'Copy image' }).click()
	await expect(panel.getByRole('alert')).toHaveCount(0)
	expect((await paste(panel.page())).fileTypes).toContain('image/png')
	await zing.close()
})

test('QR Code says so when the text is too long', async () => {
	const zing = await launchZing()
	const panel = await openTool(zing, 'QR Code')
	await panel.getByRole('textbox', { name: 'Text' }).fill('a'.repeat(5000))
	await expect(panel.getByRole('alert')).toContainText('too long')
	await expect(panel.locator('canvas')).toHaveCount(0)
	await zing.close()
})

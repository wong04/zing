import { expect, test } from '@playwright/test'
import { launchZing } from './zing'

test('Zing starts with one window showing a tab', async () => {
	const { app, chrome } = await launchZing()
	await expect(chrome.locator('.chrome')).toBeVisible()
	await expect(chrome.locator('[role="tab"]')).toHaveCount(1)
	expect(await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length)).toBe(1)
	await app.close()
})

// Tests run while someone may be using the PC: windows must not appear on screen or take focus.
test('test windows open off-screen and never take focus', async () => {
	const { app } = await launchZing()
	const state = () => app.evaluate(({ BrowserWindow, screen }) => {
		const window = BrowserWindow.getAllWindows()[0]!
		const bounds = window.getBounds()
		const onAnyDisplay = screen.getAllDisplays().some(({ bounds: d }) => bounds.x < d.x + d.width && bounds.x + bounds.width > d.x && bounds.y < d.y + d.height && bounds.y + bounds.height > d.y)
		return { onAnyDisplay, focused: window.isFocused(), visible: window.isVisible() }
	})
	await expect.poll(state).toEqual({ onAnyDisplay: false, focused: false, visible: true })
	await app.close()
})

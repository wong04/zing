import { expect, test } from '@playwright/test'
import { launchZing } from './zing'

test('Zing starts with one window showing a tab', async () => {
	const { app, chrome } = await launchZing()
	await expect(chrome.locator('.chrome')).toBeVisible()
	await expect(chrome.locator('[role="tab"]')).toHaveCount(1)
	expect(await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length)).toBe(1)
	await app.close()
})

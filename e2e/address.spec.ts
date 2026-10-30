import { expect, test } from '@playwright/test'
import { activeTabId, html, serve, tabsOf } from './fixtures'
import { launchZing } from './zing'

test('typing an address and pressing Enter navigates the active tab', async () => {
	const site = await serve({ '/typed': html('<title>typed</title><p>typed</p>') })
	const { app, chrome } = await launchZing()
	const address = chrome.getByRole('textbox', { name: 'Address and search bar' })
	await address.click()
	await address.fill(`http://127.0.0.1:${site.port}/typed`)
	await address.press('Enter')
	const id = await activeTabId(chrome)
	await expect.poll(async () => (await tabsOf(chrome)).tabs.find((t) => t.id === id)?.url).toBe(`http://127.0.0.1:${site.port}/typed`)
	await app.close()
	await site.close()
})

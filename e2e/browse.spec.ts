import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect } from '@playwright/test'
import { launchZing, test, pageWhere } from './zing'

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		if (req.url === '/one') return res.end('<!doctype html><title>Page one</title><p>one</p>')
		if (req.url === '/two') return res.end('<!doctype html><title>Page two</title><p>two</p>')
		res.statusCode = 404
		res.end()
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

test('the React chrome loads in WebView2 with one tab', async () => {
	const zing = await launchZing()
	await expect(zing.chrome.locator('.chrome')).toBeVisible()
	await expect(zing.chrome.locator('[role="tab"]')).toHaveCount(1)
	await zing.close()
})

test('typing an address loads it in the page and the tab shows its title', async () => {
	const zing = await launchZing()
	// The chrome draws its first tab a moment after it starts; typing before then has no tab to go to.
	await expect(zing.chrome.locator('[role="tab"]')).toHaveCount(1)
	const address = zing.chrome.getByRole('textbox', { name: 'Address and search bar' })
	await address.click()
	await address.fill(`${base}/one`)
	await address.press('Enter')
	await pageWhere(zing.browser, (p) => p.url() === `${base}/one`)
	await expect(zing.chrome.locator('[role="tab"]')).toContainText('Page one')
	await zing.close()
})

test('back and forward go through the page history', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/one` } })
	await pageWhere(zing.browser, (p) => p.url() === `${base}/one`)
	// The chrome draws its first tab a moment after the page loads; typing before then has no tab to go to.
	await expect(zing.chrome.locator('[role="tab"]')).toContainText('Page one')
	const address = zing.chrome.getByRole('textbox', { name: 'Address and search bar' })
	await address.click()
	await address.fill(`${base}/two`)
	await address.press('Enter')
	await expect(zing.chrome.locator('[role="tab"]')).toContainText('Page two')
	// The real buttons: enabled from the page's history, then going back and forward through it.
	const back = zing.chrome.getByRole('button', { name: /^Back/ })
	const forward = zing.chrome.getByRole('button', { name: /^Forward/ })
	await expect(back).toBeEnabled()
	await back.click()
	await expect(zing.chrome.locator('[role="tab"]')).toContainText('Page one')
	await expect(forward).toBeEnabled()
	await forward.click()
	await expect(zing.chrome.locator('[role="tab"]')).toContainText('Page two')
	await zing.close()
})

test('the top band leaves room for the window buttons, with tabs on the side or on top', async () => {
	const zing = await launchZing()
	const padding = (selector: string) => () => zing.chrome.locator(selector).evaluate((el) => parseFloat((globalThis as unknown as { getComputedStyle(e: unknown): { paddingRight: string } }).getComputedStyle(el).paddingRight))
	// Minimise, maximise and close take about 138 DIPs; nothing of the band may sit under them.
	await expect.poll(padding('.toolbar')).toBeGreaterThan(100)
	await zing.chrome.evaluate(() => (globalThis as unknown as { zing: { setSetting(k: string, v: unknown): Promise<void> } }).zing.setSetting('tabBarPosition', 'top'))
	await expect.poll(padding('.tabstrip')).toBeGreaterThan(100)
	await zing.close()
})

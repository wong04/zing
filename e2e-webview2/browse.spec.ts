import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, test } from '@playwright/test'
import { launchZing, pageWhere } from './zing'

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
	const address = zing.chrome.getByRole('textbox', { name: 'Address and search bar' })
	await address.click()
	await address.fill(`${base}/one`)
	await address.press('Enter')
	await pageWhere(zing.browser, (p) => p.url() === `${base}/one`)
	await expect(zing.chrome.locator('[role="tab"]')).toContainText('Page one')
	await zing.close()
})

test('back and forward go through the page history', async () => {
	const zing = await launchZing({ ZING_START_URL: `${base}/one` })
	await pageWhere(zing.browser, (p) => p.url() === `${base}/one`)
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

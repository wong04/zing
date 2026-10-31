import { expect, test, type Browser } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pages, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

const isStartPage = (url: string) => url === 'zing://home' || url === 'zing://home/'
const startPage = (browser: Browser) => pageWhere(browser, (p) => isStartPage(p.url()))

test('a new window opens the start page with its tiles, titled New Tab', async () => {
	const zing = await launchZing({ env: { ZING_E2E_OFFLINE: '1' } })
	const start = await startPage(zing.browser)
	await expect(start.locator('.tile')).toHaveCount(8)
	await expect(zing.chrome.locator('[role="tab"]')).toContainText('New Tab')
	await zing.close()
})

test('the start page search goes through the address rule and the search engine', async () => {
	const zing = await launchZing({ env: { ZING_E2E_OFFLINE: '1' } })
	const start = await startPage(zing.browser)
	await start.locator('input[name=q]').fill('hello world')
	await start.locator('input[name=q]').press('Enter')
	await expect
		.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs[0]?.url)
		.toBe('https://www.google.com/search?q=hello%20world')
	await zing.close()
})

test('a changed accent restyles the open start page without reloading it', async () => {
	const zing = await launchZing({ env: { ZING_E2E_OFFLINE: '1' } })
	const start = await startPage(zing.browser)
	await start.evaluate(() => ((globalThis as unknown as { marker: number }).marker = 1))
	const css = () => start.evaluate(() => (globalThis as unknown as { document: { documentElement: { outerHTML: string } } }).document.documentElement.outerHTML.length)
	const before = await css()
	await zing.chrome.evaluate(() => window.zing.setSetting('accent', 'purple'))
	await expect.poll(css).not.toBe(before)
	expect(await start.evaluate(() => (globalThis as unknown as { marker?: number }).marker)).toBe(1)
	await zing.close()
})

test('closing the last tab leaves a fresh start page', async () => {
	const zing = await launchZing({ env: { ZING_E2E_OFFLINE: '1' } })
	await startPage(zing.browser)
	const { activeId } = await zing.chrome.evaluate(() => window.zing.getTabs())
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), activeId!)
	await expect.poll(async () => { const urls = (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.url); return urls.length === 1 && isStartPage(urls[0]!) }).toBe(true)
	await expect.poll(() => pages(zing.browser).filter((p) => isStartPage(p.url())).length).toBe(1)
	await zing.close()
})

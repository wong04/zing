import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, test, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

let server: Server
let port = 0

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const title = decodeURIComponent((req.url ?? '/').slice(1)) || 'Home'
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>${title}</title><body>${title}</body>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	port = (server.address() as AddressInfo).port
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const url = (host: string, title: string): string => `http://${host}.localhost:${port}/${encodeURIComponent(title)}`

/** Opens a tab on a page with this title and waits until the strip shows it. */
async function openPage(chrome: Page, host: string, title: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, address]) => window.zing.navigateTab(id!, address!), [activeId!, url(host, title)])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.title).toBe(title)
	return activeId!
}

const titles = (chrome: Page) => chrome.locator('.tabs > [role="tab"]').evaluateAll((els) => els.map((e) => e.getAttribute('title')))

test('arrangements sort the strip', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'one', 'Zebra')
	await openPage(zing.chrome, 'two', 'Apple')
	await openPage(zing.chrome, 'three', 'Mango')

	await zing.chrome.evaluate(() => window.zing.setTabOrganization('alphabetical'))
	await expect.poll(async () => (await titles(zing.chrome)).filter((t) => t !== 'New Tab')).toEqual(['Apple', 'Mango', 'Zebra'])

	await zing.chrome.evaluate(() => window.zing.setTabOrganization('newest'))
	await expect.poll(async () => (await titles(zing.chrome)).slice(0, 3)).toEqual(['Mango', 'Apple', 'Zebra'])
	await zing.close()
})

test('Dropdown style stacks a site and lists its pages in the overlay', async () => {
	const zing = await launchZing()
	const first = await openPage(zing.chrome, 'a.site', 'Inbox')
	await openPage(zing.chrome, 'b.site', 'Docs')
	await openPage(zing.chrome, 'solo', 'Alone')

	await zing.chrome.evaluate(() => window.zing.setTabDropdowns(true))
	const stack = zing.chrome.locator('.tab.stack')
	await expect(stack).toHaveCount(1)
	await expect(stack.locator('.stack-count')).toHaveText('2')

	await stack.click()
	const dropdown = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay-stack'))
	await expect(dropdown.locator('.stack-row')).toHaveCount(2)
	await expect(zing.chrome.locator('.tab.stack.open')).toHaveCount(1)

	await dropdown.locator(`.stack-row[data-tab-id="${first}"]`).click()
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId).toBe(first)
	await expect(zing.chrome.locator('.tab.stack.open')).toHaveCount(0)
	await zing.close()
})

// Phase 4 PR 1: the tab rail on the side (docs/phase-4.md).
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, layers } from './zing'

declare const window: { readonly zing: ZingApi }

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const title = decodeURIComponent((req.url ?? '/').slice(1)) || 'Home'
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>${title}</title><body>${title}</body>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

async function openPage(chrome: Page, title: string): Promise<void> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}/${title}`])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.title).toBe(title)
}

const rows = (chrome: Page) => chrome.locator('.rail-tabs [role="tab"]')
const rowTitles = async (chrome: Page) => (await rows(chrome).locator('.rail-row-title').allInnerTexts()).filter((t) => t !== 'New Tab')

/** The page on screen, as the window really lays it out. */
async function shownPage(chrome: Page, title: string) {
	return (await layers(chrome)).find((l) => l.url === `${base}/${title}` && l.visible)
}

test('tabs sit in a rail on the left by default, with the page beside it', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	await expect(zing.chrome.getByRole('navigation', { name: 'Tab rail' })).toBeVisible()
	await expect(zing.chrome.locator('.tabstrip')).toHaveCount(0)
	await expect.poll(() => rowTitles(zing.chrome)).toContain('Alpha')
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(240)
	await zing.close()
})

test('switching between top, left and right keeps the tabs and moves the page', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	await openPage(zing.chrome, 'Beta')
	const ids = async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.id)
	const before = await ids()
	const window_ = (await layers(zing.chrome)).find((l) => l.url.endsWith('#chrome'))!

	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
	await expect(zing.chrome.locator('.tabstrip')).toBeVisible()
	// On top the app sidebar is its own column again.
	await expect.poll(async () => (await shownPage(zing.chrome, 'Beta'))?.x).toBe(204)

	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'right'))
	await expect(zing.chrome.getByRole('navigation', { name: 'Tab rail' })).toBeVisible()
	await expect.poll(async () => (await shownPage(zing.chrome, 'Beta'))?.width).toBe(window_.width - 204 - 240)

	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'left'))
	await expect.poll(async () => (await shownPage(zing.chrome, 'Beta'))?.x).toBe(240)
	expect(await ids()).toEqual(before)
	for (const page of await layers(zing.chrome)) {
		expect(Number.isInteger(page.x) && Number.isInteger(page.y), page.url).toBe(true)
	}
	await zing.close()
})

test('dragging a row up and down the rail moves its tab', async () => {
	const zing = await launchZing()
	for (const title of ['Zebra', 'Apple', 'Mango']) await openPage(zing.chrome, title)
	const named = async () => (await rowTitles(zing.chrome)).filter((t) => ['Zebra', 'Apple', 'Mango'].includes(t))
	await expect.poll(named).toEqual(['Zebra', 'Apple', 'Mango'])
	const from = (await rows(zing.chrome).filter({ hasText: 'Zebra' }).boundingBox())!
	const to = (await rows(zing.chrome).filter({ hasText: 'Mango' }).boundingBox())!
	await zing.chrome.mouse.move(from.x + 40, from.y + from.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(to.x + 40, to.y + to.height - 4, { steps: 12 })
	await zing.chrome.mouse.up()
	await expect.poll(named).toEqual(['Apple', 'Mango', 'Zebra'])
	await zing.close()
})

test('dragging the rail’s edge resizes it, and the page follows', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	const edge = (await zing.chrome.getByRole('separator', { name: 'Resize tabs' }).boundingBox())!
	await zing.chrome.mouse.move(edge.x + edge.width / 2, edge.y + 200)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(edge.x + edge.width / 2 + 60, edge.y + 200, { steps: 6 })
	await zing.chrome.mouse.up()
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getSettings())).verticalTabWidth).toBe(300)
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(300)
	await zing.close()
})

test('hiding the sidebar gives the rail’s column to the page', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(240)
	await zing.chrome.getByRole('button', { name: 'Hide sidebar (Ctrl+S)' }).click()
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(0)
	await expect(zing.chrome.getByRole('navigation', { name: 'Tab rail' })).toHaveCount(0)
	await zing.chrome.evaluate(() => window.zing.setSetting('sidebarPinned', true))
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(240)
	await zing.close()
})

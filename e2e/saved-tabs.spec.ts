// Phase 4 PR 2: Favourites and pinned items as saved tabs (docs/phase-4.md, part B).
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, pages } from './zing'

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

const tabsOf = (chrome: Page) => chrome.evaluate(() => window.zing.getTabs())

async function openPage(chrome: Page, title: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await tabsOf(chrome)
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}/${title}`])
	await expect.poll(async () => (await tabsOf(chrome)).tabs.find((t) => t.id === activeId)?.title).toBe(title)
	return activeId!
}

const pinnedSection = (chrome: Page) => chrome.getByRole('region', { name: 'Pinned' })
const favourites = (chrome: Page) => chrome.getByRole('list', { name: 'Favourites' })
const todayTitles = async (chrome: Page) => chrome.locator('.rail-tabs [role="tab"] .rail-row-title').allInnerTexts()

test('a pinned tab is saved in Pinned; closing its tab keeps the item, and the item opens a fresh tab at its address', async () => {
	const zing = await launchZing()
	const id = await openPage(zing.chrome, 'Mail')
	await zing.chrome.evaluate((tab) => window.zing.pinTab(tab), id)
	const item = pinnedSection(zing.chrome).getByRole('button', { name: 'Mail', exact: true })
	await expect(item).toBeVisible()
	await expect(item).toHaveAttribute('aria-current', 'page')
	expect(await todayTitles(zing.chrome)).not.toContain('Mail')

	await zing.chrome.evaluate((tab) => window.zing.navigateTab(tab, 'about:blank#elsewhere'), id)
	await zing.chrome.evaluate((tab) => window.zing.closeTab(tab), id)
	await expect(item).toHaveClass(/unloaded/)
	await item.click()
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.find((t) => t.itemId !== undefined)?.url).toBe(`${base}/Mail`)
	await expect(item).not.toHaveClass(/unloaded/)
	await zing.close()
})

test('Ctrl+D makes the tab a favourite tile, and clicking the tile again switches to its tab rather than opening another', async () => {
	const zing = await launchZing()
	const mail = await openPage(zing.chrome, 'Mail')
	await zing.chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
	const tile = favourites(zing.chrome).getByRole('listitem', { name: 'Mail' })
	await expect(tile).toBeVisible()
	await openPage(zing.chrome, 'Other')
	const count = (await tabsOf(zing.chrome)).tabs.length
	await tile.click()
	await expect.poll(async () => (await tabsOf(zing.chrome)).activeId).toBe(mail)
	await tile.click()
	expect((await tabsOf(zing.chrome)).tabs.length).toBe(count)
	await zing.close()
})

test('saved items survive a relaunch, but their pages do not', async () => {
	const first = await launchZing()
	const id = await openPage(first.chrome, 'Mail')
	await first.chrome.evaluate((tab) => window.zing.pinTab(tab), id)
	await expect(pinnedSection(first.chrome).getByRole('button', { name: 'Mail', exact: true })).toBeVisible()
	await first.close()

	const second = await launchZing({ userDataDir: first.userDataDir })
	const item = pinnedSection(second.chrome).getByRole('button', { name: 'Mail', exact: true })
	await expect(item).toHaveClass(/unloaded/)
	expect(pages(second.browser).some((p) => p.url() === `${base}/Mail`)).toBe(false)
	await second.close()
})

test('dragging a tab into Pinned saves it there, and dragging the item back among the tabs unpins it', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Notes')
	const row = zing.chrome.locator('.rail-tabs [role="tab"]').filter({ hasText: 'Notes' })
	const from = (await row.boundingBox())!
	const into = (await pinnedSection(zing.chrome).boundingBox())!
	await zing.chrome.mouse.move(from.x + 40, from.y + from.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(into.x + 40, into.y + into.height / 2, { steps: 12 })
	await zing.chrome.mouse.up()
	const item = pinnedSection(zing.chrome).getByRole('button', { name: 'Notes', exact: true })
	await expect(item).toBeVisible()
	await expect.poll(() => todayTitles(zing.chrome)).not.toContain('Notes')

	const held = (await item.boundingBox())!
	const today = (await zing.chrome.locator('.rail-tabs').boundingBox())!
	await zing.chrome.mouse.move(held.x + 40, held.y + held.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(today.x + 40, today.y + today.height - 20, { steps: 12 })
	await zing.chrome.mouse.up()
	await expect(item).toHaveCount(0)
	await expect.poll(() => todayTitles(zing.chrome)).toContain('Notes')
	await zing.close()
})

test('with tabs on the side the Favourites bar gives way to the tiles; on top it is back', async () => {
	const zing = await launchZing()
	await expect(favourites(zing.chrome)).toBeVisible()
	await expect(zing.chrome.getByRole('toolbar', { name: 'Favourites' })).toHaveCount(0)
	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
	await expect(zing.chrome.getByRole('toolbar', { name: 'Favourites' })).toBeVisible()
	await zing.close()
})

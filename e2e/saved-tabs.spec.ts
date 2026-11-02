// Phase 4 PR 2: Favourites and pinned items as saved tabs (docs/phase-4.md, part B).
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, pages } from './zing'
import { FAVOURITES_ID } from '../src/shared/library/library'

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

test('Ctrl+D makes the tab a favourite ribbon, and clicking the ribbon again switches to its tab rather than opening another', async () => {
	const zing = await launchZing()
	const mail = await openPage(zing.chrome, 'Mail')
	await zing.chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
	const ribbon = favourites(zing.chrome).getByRole('listitem', { name: 'Mail' })
	await expect(ribbon).toBeVisible()
	await openPage(zing.chrome, 'Other')
	const count = (await tabsOf(zing.chrome)).tabs.length
	await ribbon.click()
	await expect.poll(async () => (await tabsOf(zing.chrome)).activeId).toBe(mail)
	await ribbon.click()
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

test('with tabs on the side the Favourites bar gives way to the ribbons; on top it is back', async () => {
	const zing = await launchZing()
	await expect(favourites(zing.chrome)).toBeVisible()
	await expect(zing.chrome.getByRole('toolbar', { name: 'Favourites' })).toHaveCount(0)
	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
	await expect(zing.chrome.getByRole('toolbar', { name: 'Favourites' })).toBeVisible()
	await zing.close()
})

async function addFavourite(chrome: Page, title: string): Promise<void> {
	await openPage(chrome, title)
	await chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
}

const favouriteRibbons = (chrome: Page) => favourites(chrome).locator('.rail-ribbon:not(.more)')
const moreButton = (chrome: Page, count: number) => chrome.getByRole('button', { name: `${count} more favourites` })

test('favourites are ribbons with a count: as many as the width fits, the rest behind a "+N" button', async () => {
	const zing = await launchZing()
	for (let i = 1; i <= 16; i++) await addFavourite(zing.chrome, `Fav${i}`)
	await zing.chrome.evaluate(() => window.zing.setSetting('verticalTabWidth', 240))
	await expect(favouriteRibbons(zing.chrome)).toHaveCount(4)
	await expect(moreButton(zing.chrome, 12)).toBeVisible()
	await zing.close()
})

test('no "+N" when the favourites all fit', async () => {
	const zing = await launchZing()
	for (let i = 1; i <= 3; i++) await addFavourite(zing.chrome, `Fav${i}`)
	await zing.chrome.evaluate(() => window.zing.setSetting('verticalTabWidth', 240))
	await expect(favouriteRibbons(zing.chrome)).toHaveCount(3)
	await expect(zing.chrome.getByRole('button', { name: /more favourites/ })).toHaveCount(0)
	await zing.close()
})

test('resizing the rail changes how many ribbons show', async () => {
	const zing = await launchZing()
	for (let i = 1; i <= 16; i++) await addFavourite(zing.chrome, `Fav${i}`)
	await zing.chrome.evaluate(() => window.zing.setSetting('verticalTabWidth', 160))
	await expect(favouriteRibbons(zing.chrome)).toHaveCount(2)
	await expect(moreButton(zing.chrome, 14)).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.setSetting('verticalTabWidth', 240))
	await expect(favouriteRibbons(zing.chrome)).toHaveCount(4)
	await zing.close()
})

test('an empty Favourites is one faint ribbon, and a tab dragged onto it becomes a favourite', async () => {
	const zing = await launchZing()
	const empty = favourites(zing.chrome).locator('.rail-ribbon.empty')
	await expect(empty).toHaveAttribute('title', 'Drag a tab here, or press Ctrl+D')
	await expect(favourites(zing.chrome).locator('.rail-hint')).toHaveCount(0)

	await openPage(zing.chrome, 'Notes')
	const row = zing.chrome.locator('.rail-tabs [role="tab"]').filter({ hasText: 'Notes' })
	const from = (await row.boundingBox())!
	const into = (await empty.boundingBox())!
	await zing.chrome.mouse.move(from.x + 40, from.y + from.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(into.x + into.width / 2, into.y + into.height / 2, { steps: 12 })
	await zing.chrome.mouse.up()
	await expect(favourites(zing.chrome).getByRole('listitem', { name: 'Notes' })).toBeVisible()
	await expect(empty).toHaveCount(0)
	await zing.close()
})

test('a folder ribbon shows the folder icon and still opens the folder menu, not a tab', async () => {
	const zing = await launchZing()
	const tab = await openPage(zing.chrome, 'Notes')
	const folder = (await zing.chrome.evaluate(([id, parent]) => window.zing.applyLibrary({ type: 'newFolder', ids: [] }).then(async (made) => {
		await window.zing.applyLibrary({ type: 'moveInto', id: made!, parent: parent!, to: 0 })
		await window.zing.saveTab(id!, made!, 0)
		return made
	}), [tab, FAVOURITES_ID]))!
	const ribbon = favourites(zing.chrome).locator(`[data-drop-item="${folder}"]`)
	await expect(ribbon.locator('svg')).toBeVisible()
	const tabs = (await tabsOf(zing.chrome)).tabs.length
	await ribbon.click()
	await expect.poll(() => zing.menus().length).toBe(1)
	expect((await tabsOf(zing.chrome)).tabs.length).toBe(tabs)
	await zing.close()
})

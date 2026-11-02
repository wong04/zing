// Phase 4 PR 2: Favourites and pinned items as saved tabs (docs/phase-4.md, part B).
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, pages, type Launched } from './zing'
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

const morePanel = (chrome: Page) => chrome.locator('#rail-favourites-panel')
const panelTitles = (chrome: Page) => morePanel(chrome).locator('.rail-panel-row .rail-row-title').allInnerTexts()
const panelRow = (chrome: Page, title: string) => morePanel(chrome).getByRole('listitem', { name: title, exact: true })
async function activeTitle(chrome: Page): Promise<string | undefined> {
	const { tabs, activeId } = await tabsOf(chrome)
	return tabs.find((t) => t.id === activeId)?.title
}

async function sixteenFavourites(): Promise<Launched> {
	const zing = await launchZing()
	for (let i = 1; i <= 16; i++) await addFavourite(zing.chrome, `Fav${i}`)
	await zing.chrome.evaluate(() => window.zing.setSetting('verticalTabWidth', 240))
	return zing
}

test('the panel lists the rest with names: "+N" opens it, and each row opens its favourite', async () => {
	const zing = await sixteenFavourites()
	const more = moreButton(zing.chrome, 12)
	await expect(more).toHaveAttribute('aria-expanded', 'false')
	await more.click()
	await expect(more).toHaveAttribute('aria-expanded', 'true')
	const names = Array.from({ length: 12 }, (_, i) => `Fav${i + 5}`)
	await expect.poll(() => panelTitles(zing.chrome)).toEqual(names)
	for (const name of names) {
		await panelRow(zing.chrome, name).click()
		await expect(morePanel(zing.chrome)).toHaveCount(0)
		await expect.poll(() => activeTitle(zing.chrome)).toBe(name)
		await moreButton(zing.chrome, 12).click()
	}
	await zing.close()
})

test('a favourite dragged from the panel onto a ribbon becomes visible', async () => {
	const zing = await sixteenFavourites()
	await moreButton(zing.chrome, 12).click()
	// On a short window the panel scrolls, and the last row starts out of view.
	await panelRow(zing.chrome, 'Fav16').scrollIntoViewIfNeeded()
	const from = (await panelRow(zing.chrome, 'Fav16').boundingBox())!
	const onto = (await favouriteRibbons(zing.chrome).first().boundingBox())!
	await zing.chrome.mouse.move(from.x + 40, from.y + from.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(onto.x + onto.width / 2, onto.y + onto.height / 2, { steps: 12 })
	// A held row is not clipped by the panel while it is dragged.
	await expect.poll(() => morePanel(zing.chrome).evaluate((el) => getComputedStyle(el).overflowY)).toBe('visible')
	await zing.chrome.evaluate(() => globalThis.dispatchEvent(new Event('blur')))
	await expect(morePanel(zing.chrome)).toBeVisible()
	await zing.chrome.mouse.up()
	await expect(favouriteRibbons(zing.chrome).first()).toHaveAttribute('aria-label', 'Fav16')
	await expect(favouriteRibbons(zing.chrome)).toHaveCount(4)
	await expect(panelRow(zing.chrome, 'Fav4')).toBeVisible()
	await expect(moreButton(zing.chrome, 12)).toBeVisible()
	await zing.close()
})

test('a ribbon dragged into the panel leaves the ribbons', async () => {
	const zing = await sixteenFavourites()
	await moreButton(zing.chrome, 12).click()
	const from = (await favouriteRibbons(zing.chrome).first().boundingBox())!
	const into = (await morePanel(zing.chrome).boundingBox())!
	await zing.chrome.mouse.move(from.x + from.width / 2, from.y + from.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(into.x + into.width / 2, into.y + into.height - 4, { steps: 12 })
	await zing.chrome.mouse.up()
	await expect(favouriteRibbons(zing.chrome)).toHaveCount(4)
	await expect(favouriteRibbons(zing.chrome).first()).toHaveAttribute('aria-label', 'Fav2')
	await expect(panelRow(zing.chrome, 'Fav1')).toBeVisible()
	await expect(moreButton(zing.chrome, 12)).toBeVisible()
	await zing.close()
})

test('the panel closes on Escape, returning focus to "+N", and when the chrome loses focus', async () => {
	const zing = await sixteenFavourites()
	const more = moreButton(zing.chrome, 12)
	await more.click()
	await panelRow(zing.chrome, 'Fav5').focus()
	await zing.chrome.keyboard.press('Escape')
	await expect(morePanel(zing.chrome)).toHaveCount(0)
	await expect(more).toBeFocused()

	await more.click()
	await expect(morePanel(zing.chrome)).toBeVisible()
	await zing.chrome.evaluate(() => globalThis.dispatchEvent(new Event('blur')))
	await expect(morePanel(zing.chrome)).toHaveCount(0)

	await more.click()
	await expect(morePanel(zing.chrome)).toBeVisible()
	await zing.chrome.locator('.rail-head .rail-drag').click()
	await expect(morePanel(zing.chrome)).toHaveCount(0)
	await zing.close()
})

test('"+N" shows when it holds the active favourite', async () => {
	const zing = await sixteenFavourites()
	const more = moreButton(zing.chrome, 12)
	await expect(more).toHaveClass(/holds-active/)
	await favouriteRibbons(zing.chrome).first().click()
	await expect(more).not.toHaveClass(/holds-active/)
	await more.click()
	await panelRow(zing.chrome, 'Fav16').click()
	await expect(more).toHaveClass(/holds-active/)
	await zing.close()
})

test('collapsed, the panel is an icon column with tooltips, and all favourites are reachable', async () => {
	const zing = await sixteenFavourites()
	await zing.chrome.evaluate(() => window.zing.setSetting('verticalTabsCollapsed', true))
	await moreButton(zing.chrome, 16).click()
	const rows = morePanel(zing.chrome).getByRole('listitem')
	await expect(rows).toHaveCount(16)
	await expect(rows.first()).toHaveAttribute('title', 'Fav1')
	await expect(morePanel(zing.chrome).locator('.rail-row-title').first()).toBeHidden()
	await expect(zing.chrome.locator('.rail-panel-head')).toBeHidden()
	await panelRow(zing.chrome, 'Fav9').click()
	await expect(morePanel(zing.chrome)).toHaveCount(0)
	await expect.poll(() => activeTitle(zing.chrome)).toBe('Fav9')
	await zing.close()
})

test('Ctrl+D with a full row raises the count', async () => {
	const zing = await sixteenFavourites()
	await openPage(zing.chrome, 'Fav17')
	await zing.chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
	await expect(moreButton(zing.chrome, 13)).toBeVisible()
	await expect(favouriteRibbons(zing.chrome)).toHaveCount(4)
	await zing.close()
})

test('a panel left open does not reopen when the rail widens to fit all and narrows again', async () => {
	const zing = await launchZing()
	for (let i = 1; i <= 6; i++) await addFavourite(zing.chrome, `Fav${i}`)
	await zing.chrome.evaluate(() => window.zing.setSetting('verticalTabWidth', 240))
	await moreButton(zing.chrome, 2).click()
	await expect(morePanel(zing.chrome)).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.setSetting('verticalTabWidth', 480))
	await expect(zing.chrome.getByRole('button', { name: /more favourites/ })).toHaveCount(0)
	await zing.chrome.evaluate(() => window.zing.setSetting('verticalTabWidth', 240))
	await expect(moreButton(zing.chrome, 2)).toBeVisible()
	await expect(morePanel(zing.chrome)).toHaveCount(0)
	await zing.close()
})

test('an empty favourites ribbon is outlined, not just filled', async () => {
	const zing = await launchZing()
	const empty = favourites(zing.chrome).locator('.rail-ribbon.empty')
	await expect(empty).toBeVisible()
	const outline = await empty.evaluate((el) => {
		const style = getComputedStyle(el, '::before')
		return { content: style.content, position: style.position }
	})
	expect(outline.content).not.toBe('none')
	expect(outline.position).toBe('absolute')
	await zing.close()
})

test('a ribbon opens its favourite from the keyboard, with Enter or Space', async () => {
	const zing = await sixteenFavourites()
	const ribbons = favouriteRibbons(zing.chrome)
	await ribbons.nth(0).focus()
	await zing.chrome.keyboard.press('Enter')
	await expect.poll(() => activeTitle(zing.chrome)).toBe('Fav1')
	await ribbons.nth(1).focus()
	await zing.chrome.keyboard.press(' ')
	await expect.poll(() => activeTitle(zing.chrome)).toBe('Fav2')
	await zing.close()
})

test('a tab dropped on "+N" is saved as the first of the rest', async () => {
	const zing = await sixteenFavourites()
	await openPage(zing.chrome, 'Notes')
	const row = zing.chrome.locator('.rail-tabs [role="tab"]').filter({ hasText: 'Notes' })
	const from = (await row.boundingBox())!
	const onto = (await moreButton(zing.chrome, 12).boundingBox())!
	await zing.chrome.mouse.move(from.x + 40, from.y + from.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(onto.x + onto.width / 2, onto.y + onto.height / 2, { steps: 12 })
	await zing.chrome.mouse.up()
	await expect(moreButton(zing.chrome, 13)).toBeVisible()
	await expect(favouriteRibbons(zing.chrome)).toHaveCount(4)
	await moreButton(zing.chrome, 13).click()
	const rest = Array.from({ length: 12 }, (_, i) => `Fav${i + 5}`)
	await expect.poll(() => panelTitles(zing.chrome)).toEqual(['Notes', ...rest])
	await zing.close()
})

import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { favourites, FAVOURITES_ID } from '../src/shared/library/library'
import { launchZing, test, layers, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

/** The part of the page's DOM these tests read; this file has no DOM types, so the globals are reached through globalThis. */
interface Dom {
	readonly document: {
		readonly body: { append(node: unknown): void }
		createElement(tag: string): { style: { backgroundColor: string; color: string }; remove(): void }
	}
	getComputedStyle(element: unknown): { backgroundColor: string; color: string }
}

interface Painted {
	readonly background: string
	readonly color: string
}

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

/** These tests are about the top strip; tabs are on the side by default (docs/phase-4.md). */
async function launchWithStrip() {
	const zing = await launchZing()
	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
	await expect(zing.chrome.locator('.tabstrip')).toBeVisible()
	return zing
}

/** Opens a tab on a page with this title and waits until the strip shows it. */
async function openPage(chrome: Page, host: string, title: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, address]) => window.zing.navigateTab(id!, address!), [activeId!, url(host, title)])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.title).toBe(title)
	return activeId!
}

const titles = (chrome: Page) => chrome.locator('.tabs > [role="tab"]').evaluateAll((els) => els.map((e) => e.getAttribute('title')))

test('dragging a tab along the strip moves it, and the order is the tabs’ own', async () => {
	const zing = await launchWithStrip()
	await openPage(zing.chrome, 'one', 'Zebra')
	await openPage(zing.chrome, 'two', 'Apple')
	await openPage(zing.chrome, 'three', 'Mango')
	const named = async () => (await titles(zing.chrome)).filter((t) => t !== 'New Tab')
	await expect.poll(named).toEqual(['Zebra', 'Apple', 'Mango'])
	const from = (await zing.chrome.locator('.tabs > [role="tab"][title="Zebra"]').boundingBox())!
	const to = (await zing.chrome.locator('.tabs > [role="tab"][title="Mango"]').boundingBox())!
	// Pointer events in the chrome's own view, in steps, so the strip sees a drag rather than a click.
	await zing.chrome.mouse.move(from.x + from.width / 2, from.y + from.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(to.x + to.width - 4, to.y + to.height / 2, { steps: 12 })
	await zing.chrome.mouse.up()
	await expect.poll(named).toEqual(['Apple', 'Mango', 'Zebra'])
	expect((await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.title).filter((t) => t !== 'New Tab')).toEqual(['Apple', 'Mango', 'Zebra'])
	await zing.close()
})

test('arrangements sort the strip', async () => {
	const zing = await launchWithStrip()
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
	const zing = await launchWithStrip()
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

	// CDP mouse input never reaches this one view (it does reach the chrome and #pageui), so the row
	// gets DOM pointer events; real clicks are checked by hand (docs/webview2-migration.md, W3d).
	const row = dropdown.locator(`.stack-row[data-tab-id="${first}"]`)
	await row.dispatchEvent('pointerdown', { button: 0, pointerId: 1, clientY: 10 })
	await row.dispatchEvent('pointerup', { button: 0, pointerId: 1, clientY: 10 })
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId).toBe(first)
	await expect(zing.chrome.locator('.tab.stack.open')).toHaveCount(0)
	await zing.close()
})

test('the tab overview shows a card per tab with pictures, and closes again', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'one', 'First')
	await openPage(zing.chrome, 'two', 'Second')
	await zing.chrome.evaluate(() => window.zing.runCommand('tabs.overview'))
	const overview = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay-overview'))
	// The start page tab plus the two opened.
	await expect(overview.locator('.overview-card')).toHaveCount(3)
	await expect(overview.locator('.overview-thumb img')).not.toHaveCount(0)
	await zing.chrome.evaluate(() => window.zing.closeOverview())
	await expect(overview.locator('.overview-card')).toHaveCount(0)
	await zing.close()
})

test('a reopened tab shows its page in front of the chrome', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'one', 'Reopened')
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.close'))
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.reopenClosed'))
	// A page started after the chrome went under it, so the reopened tab showed blank.
	await expect
		.poll(async () => {
			const shown = await layers(zing.chrome)
			const page = shown.find((l) => l.url.endsWith('/Reopened') && l.visible)
			const chrome = shown.find((l) => l.url.endsWith('#chrome'))
			return page !== undefined && chrome !== undefined && page.depth < chrome.depth
		})
		.toBe(true)
	await zing.close()
})

/** The fill and ink a pair of theme variables paint, as the page computes them, to compare with toHaveCSS. */
async function paintOf(chrome: Page, fill: string, ink: string): Promise<Painted> {
	return chrome.evaluate(
		([fillVar, inkVar]) => {
			const dom = globalThis as unknown as Dom
			const probe = dom.document.createElement('i')
			probe.style.backgroundColor = `var(${fillVar})`
			probe.style.color = `var(${inkVar})`
			dom.document.body.append(probe)
			const style = dom.getComputedStyle(probe)
			const painted = { background: style.backgroundColor, color: style.color }
			probe.remove()
			return painted
		},
		[fill, ink] as const,
	)
}

/** The shade a tab takes by its place among the strip's children (nth-child, 1-based), as the card set repeats. */
const cardPaint = (chrome: Page, index: number) => paintOf(chrome, `--card-${(index % 3) + 1}`, `--card-ink-${(index % 3) + 1}`)

async function expectPaint(tab: Locator, paint: Painted): Promise<void> {
	await expect(tab).toHaveCSS('background-color', paint.background)
	await expect(tab).toHaveCSS('color', paint.color)
}

const stripTabs = (chrome: Page) => chrome.locator('.tabs > [role="tab"]')

test('tabs on top are folder tabs: the card shades repeat, and the active tab takes the band and meets the toolbar', async () => {
	const zing = await launchWithStrip()
	await openPage(zing.chrome, 'one', 'Zebra')
	await openPage(zing.chrome, 'two', 'Apple')
	await openPage(zing.chrome, 'three', 'Mango')
	await expect.poll(() => stripTabs(zing.chrome).count()).toBeGreaterThanOrEqual(4)

	const window_ = await paintOf(zing.chrome, '--window', '--window-ink')
	await expect(zing.chrome.locator('.tabstrip')).toHaveCSS('background-color', window_.background)
	const band = await paintOf(zing.chrome, '--band', '--band-ink')
	const count = await stripTabs(zing.chrome).count()
	for (let i = 0; i < count; i++) {
		const tab = stripTabs(zing.chrome).nth(i)
		const selected = (await tab.getAttribute('aria-selected')) === 'true'
		await expectPaint(tab, selected ? band : await cardPaint(zing.chrome, i))
	}

	const active = zing.chrome.locator('.tabs > [role="tab"].active')
	await expect(active).toHaveCount(1)
	await expect(active).toHaveCSS('border-top-left-radius', '9px')
	await expect(active).toHaveCSS('border-bottom-left-radius', '0px')
	const firstBox = (await stripTabs(zing.chrome).nth(0).boundingBox())!
	const secondBox = (await stripTabs(zing.chrome).nth(1).boundingBox())!
	expect(firstBox.x + firstBox.width - secondBox.x).toBeCloseTo(5, 0)
	await expect(zing.chrome.locator('.tabs > [role="tab"]:not(.active)').first()).toHaveCSS('box-shadow', 'none')
	await expect(active).toHaveCSS('box-shadow', 'none')
	const tabBox = (await active.boundingBox())!
	const toolbarBox = (await zing.chrome.locator('.toolbar').boundingBox())!
	expect(Math.abs(tabBox.y + tabBox.height - toolbarBox.y)).toBeLessThan(1)
	await zing.close()
})

test('pinned and stacked tabs take the same fills and stay inked', async () => {
	const zing = await launchWithStrip()
	await openPage(zing.chrome, 'a.site', 'Inbox')
	await openPage(zing.chrome, 'b.site', 'Docs')
	const solo = await openPage(zing.chrome, 'solo', 'Alone')
	await zing.chrome.evaluate((id) => window.zing.pinTab(id), solo)
	await zing.chrome.evaluate(() => window.zing.setTabDropdowns(true))
	await expect(zing.chrome.locator('.tab.stack')).toHaveCount(1)
	await expect(zing.chrome.locator('.tab.pinned')).toHaveCount(1)

	// The pinned run's divider is a child too, so each tab's place is counted among all the children.
	const children = zing.chrome.locator('.tabs > *')
	const count = await children.count()
	let tabs = 0
	for (let i = 0; i < count; i++) {
		const child = children.nth(i)
		if ((await child.getAttribute('role')) !== 'tab') continue
		tabs++
		const selected = (await child.getAttribute('aria-selected')) === 'true'
		await expectPaint(child, selected ? await paintOf(zing.chrome, '--band', '--band-ink') : await cardPaint(zing.chrome, i))
	}
	expect(tabs).toBeGreaterThanOrEqual(2)
	await zing.close()
})

test('favourites on top are ribbons 30 by 34 px, named by their title and hung from the top of a 40 px bar', async () => {
	const zing = await launchWithStrip()
	await openPage(zing.chrome, 'mail', 'Mail')
	await zing.chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
	const bar = zing.chrome.getByRole('toolbar', { name: 'Favourites' })
	await expect(bar).toHaveCSS('height', '40px')
	await expect(bar).toHaveCSS('background-color', (await paintOf(zing.chrome, '--band', '--band-ink')).background)

	const ribbon = bar.getByRole('button', { name: 'Mail', exact: true })
	await expect(ribbon).toHaveClass(/fav-chip/)
	await expect(ribbon).toHaveAttribute('title', 'Mail')
	await expect(ribbon.locator('.fav-title')).toHaveCount(0)
	await expect(ribbon).toHaveCSS('width', '30px')
	await expect(ribbon).toHaveCSS('height', '34px')
	await expectPaint(ribbon, await paintOf(zing.chrome, '--ribbon-1', '--ribbon-ink-1'))
	const ribbonBox = (await ribbon.boundingBox())!
	const barBox = (await bar.boundingBox())!
	expect(Math.abs(ribbonBox.y - barBox.y)).toBeLessThan(1)
	await zing.close()
})

test('favourites that do not fit fold into one "+N" button, which opens a menu of all of them', async () => {
	const zing = await launchWithStrip()
	const addTabs = (url: string) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'add-tabs', count: 60, url })
	await zing.chrome.evaluate(addTabs, `http://127.0.0.1:${port}/many-`)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.length).toBeGreaterThanOrEqual(60)
	const ids = (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.id)
	await zing.chrome.evaluate(
		async ([tabIds, folder]) => {
			for (const [at, id] of tabIds.entries()) await window.zing.saveTab(id, folder, at)
		},
		[ids, FAVOURITES_ID] as const,
	)

	const bar = zing.chrome.getByRole('toolbar', { name: 'Favourites' })
	const more = bar.locator('.fav-chip.more')
	await expect(more).toBeVisible()
	const shown = await bar.locator('.fav-chip:not(.more)').count()
	const hidden = ids.length - shown
	expect(hidden).toBeGreaterThan(0)
	await expect(more).toHaveText(`+${hidden}`)
	await expect(bar.getByRole('button', { name: `${hidden} more favourites`, exact: true })).toHaveAttribute('title', `${hidden} more favourites`)

	// A ribbon dropped on "+N" leaves the row, and the next one takes its place.
	const firstRibbon = bar.locator('.fav-chip:not(.more)').first()
	const firstTitle = await firstRibbon.getAttribute('title')
	await more.evaluate((element, id) => {
		const g = globalThis as unknown as { DataTransfer: new () => { setData(type: string, value: string): void }; DragEvent: new (type: string, init: object) => unknown }
		const dataTransfer = new g.DataTransfer()
		dataTransfer.setData('text/x-zing-fav', id)
		;(element as unknown as { dispatchEvent(event: unknown): void }).dispatchEvent(new g.DragEvent('drop', { bubbles: true, dataTransfer }))
	}, favourites((await zing.chrome.evaluate(() => window.zing.getLibrary())).library)[0]!.id)
	await expect(firstRibbon).not.toHaveAttribute('title', firstTitle!)

	await more.click()
	await expect.poll(() => zing.menus().length).toBe(1)
	// Every favourite, then a separator and "Open All in New Tabs".
	expect(zing.menus()[0]).toHaveLength(ids.length + 2)
	await zing.close()
})

test('contrast themes outline the tabs and ribbons instead of filling them', async () => {
	const zing = await launchWithStrip()
	await zing.chrome.emulateMedia({ forcedColors: 'active' })
	await openPage(zing.chrome, 'one', 'Zebra')
	await zing.chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))

	const active = zing.chrome.locator('.tabs > [role="tab"].active')
	await expect(active).toHaveCSS('border-top-width', '1px')
	await expect(active).toHaveCSS('background-color', /^rgba\(\d+, \d+, \d+, 0\)$/)
	await expect(active).toHaveCSS('margin-right', '0px')
	const ribbon = zing.chrome.getByRole('toolbar', { name: 'Favourites' }).getByRole('button', { name: 'Zebra', exact: true })
	await expect(ribbon).toHaveCSS('clip-path', 'none')
	await expect(ribbon).toHaveCSS('border-top-width', '1px')
	await zing.close()
})

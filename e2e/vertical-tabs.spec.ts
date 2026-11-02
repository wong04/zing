// Phase 4 PR 1: the tab rail on the side (docs/phase-4.md).
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, layers, pageWhere } from './zing'

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

/** A row as the page lays it out and paints it. */
interface RowLook {
	readonly active: boolean
	readonly top: number
	readonly height: number
	readonly right: number
	readonly background: string
	readonly color: string
	readonly shadow: string
}

async function rowLooks(chrome: Page): Promise<RowLook[]> {
	return rows(chrome).evaluateAll((els) => {
		const dom = globalThis as unknown as {
			getComputedStyle(e: unknown): { backgroundColor: string; color: string; boxShadow: string }
		}
		const nodes = els as unknown as { getAttribute(name: string): string | null; getBoundingClientRect(): { top: number; height: number; right: number } }[]
		return nodes.map((el) => {
			const box = el.getBoundingClientRect()
			const style = dom.getComputedStyle(el)
			return { active: el.getAttribute('aria-selected') === 'true', top: box.top, height: box.height, right: box.right, background: style.backgroundColor, color: style.color, shadow: style.boxShadow }
		})
	})
}

/** A CSS value as the browser computes it as a background and as text, so a token and the rgb() it paints with compare equal. */
interface Resolved {
	readonly background: string
	readonly color: string
}

function resolvedColor(chrome: Page, css: string): Promise<Resolved> {
	return chrome.evaluate((value) => {
		const dom = globalThis as unknown as {
			document: { body: { append(node: unknown): void }; createElement(tag: string): { style: { backgroundColor: string; color: string }; remove(): void } }
			getComputedStyle(node: unknown): { backgroundColor: string; color: string }
		}
		const probe = dom.document.createElement('div')
		probe.style.backgroundColor = value
		probe.style.color = value
		dom.document.body.append(probe)
		const style = dom.getComputedStyle(probe)
		const resolved = { background: style.backgroundColor, color: style.color }
		probe.remove()
		return resolved
	}, css)
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

test('a dragged card leaves a gap of one row spacing, and the neighbour slides into it', async () => {
	const zing = await launchZing()
	for (const title of ['Zebra', 'Apple', 'Mango']) await openPage(zing.chrome, title)
	const named = async () => (await rowTitles(zing.chrome)).filter((t) => ['Zebra', 'Apple', 'Mango'].includes(t))
	await expect.poll(named).toEqual(['Zebra', 'Apple', 'Mango'])
	const box = async (title: string) => (await rows(zing.chrome).filter({ hasText: title }).boundingBox())!
	const zebra = await box('Zebra')
	const apple = await box('Apple')
	// Zebra and Apple are inactive, so the rows sit exactly one spacing apart.
	await zing.chrome.mouse.move(zebra.x + 40, zebra.y + zebra.height / 2)
	await zing.chrome.mouse.down()
	await zing.chrome.mouse.move(zebra.x + 40, apple.y + apple.height / 2 + 4, { steps: 12 })
	// The slide is a 150 ms transform transition.
	await zing.chrome.waitForTimeout(300)
	expect(Math.abs(apple.y - (await box('Apple')).y - 30)).toBeLessThanOrEqual(3)
	await zing.chrome.mouse.up()
	await expect.poll(named).toEqual(['Apple', 'Zebra', 'Mango'])
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

test('collapsed, the rail is a column of icons and the page takes the rest', async () => {
	const zing = await launchZing()
	await openPage(zing.chrome, 'Alpha')
	await zing.chrome.getByRole('button', { name: 'Collapse the tab sidebar to icons' }).click()
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(48)
	const rail = zing.chrome.getByRole('navigation', { name: 'Tab rail' })
	await expect.poll(async () => (await rail.boundingBox())?.width).toBe(48)
	await expect(rail.locator('.rail-row-title').first()).toBeHidden()
	// Each row is still there, as its icon, with its title in the tooltip.
	await expect(zing.chrome.locator('.rail-tabs [role="tab"][title*="Alpha"]')).toHaveCount(1)
	await zing.chrome.getByRole('button', { name: 'Expand the tab sidebar' }).click()
	await expect.poll(async () => (await shownPage(zing.chrome, 'Alpha'))?.x).toBe(240)
	await zing.close()
})

test('three hundred tabs: every one is in the rail, rows out of view skip rendering, and switching stays quick', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate((url) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'add-tabs', count: 300, url }), `${base}/many-`)
	await expect(rows(zing.chrome)).toHaveCount(301)
	expect(await rows(zing.chrome).last().evaluate((row) => (globalThis as unknown as { getComputedStyle(e: unknown): { contentVisibility: string } }).getComputedStyle(row).contentVisibility)).toBe('auto')
	const last = (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.at(-1)!.id
	const elapsed = await zing.chrome.evaluate(async (id) => {
		const started = performance.now()
		await window.zing.activateTab(id)
		const doc = (globalThis as unknown as { document: { querySelector(s: string): { getAttribute(a: string): string | null } | null } }).document
		while (doc.querySelector(`[data-tab-id="${id}"]`)?.getAttribute('aria-selected') !== 'true') await new Promise((r) => setTimeout(r, 5))
		return performance.now() - started
	}, last)
	// From the call to the rail showing the new tab: the bridge both ways plus a render of 301 rows (PLAN.md's switch budget is 50 ms for the page itself).
	expect(elapsed).toBeLessThan(250)
	await zing.close()
})

test('tab rows are overlapping cards', async () => {
	const zing = await launchZing()
	for (const title of ['Alpha', 'Beta', 'Gamma', 'Delta']) await openPage(zing.chrome, title)
	await expect(rows(zing.chrome)).toHaveCount(5)
	const looks = await rowLooks(zing.chrome)
	for (const row of looks) if (!row.active) expect(row.height).toBe(38)
	for (let i = 0; i + 1 < looks.length; i++) {
		if (looks[i]!.active || looks[i + 1]!.active) continue
		expect(looks[i + 1]!.top - looks[i]!.top).toBe(30)
	}
	for (let i = 0; i < looks.length; i++) {
		if (looks[i]!.active) continue
		expect(looks[i]!.background).toBe((await resolvedColor(zing.chrome, 'var(--card)')).background)
	}
	await zing.close()
})

test('the active card reaches the page', async () => {
	const zing = await launchZing()
	for (const title of ['Alpha', 'Beta', 'Gamma']) await openPage(zing.chrome, title)
	await rows(zing.chrome).nth(2).click({ position: { x: 20, y: 15 } })
	await expect(rows(zing.chrome).nth(2)).toHaveAttribute('aria-selected', 'true')
	const looks = await rowLooks(zing.chrome)
	const active = looks[2]!
	expect(active.active).toBe(true)
	await expect.poll(async () => (await shownPage(zing.chrome, 'Beta'))?.x).toBe(active.right)
	expect(active.background).toBe((await resolvedColor(zing.chrome, 'var(--page)')).background)
	expect(looks.map((row) => row.shadow !== 'none')).toEqual(looks.map((row) => row.active))
	const activeBox = (await rows(zing.chrome).nth(2).boundingBox())!
	const nextIcon = (await rows(zing.chrome).nth(3).locator('.rail-row-icon').boundingBox())!
	expect(nextIcon.y).toBeGreaterThanOrEqual(activeBox.y + activeBox.height)
	await zing.close()
})

test('a hovered card keeps its shade and ink', async () => {
	const zing = await launchZing()
	for (const title of ['Alpha', 'Beta', 'Gamma', 'Delta']) await openPage(zing.chrome, title)
	await expect(rows(zing.chrome)).toHaveCount(5)
	await rows(zing.chrome).nth(1).hover()
	const hovered = (await rowLooks(zing.chrome))[1]!
	expect(hovered.background).toBe((await resolvedColor(zing.chrome, 'color-mix(in srgb, var(--card) 92%, var(--card-ink))')).background)
	expect(hovered.color).toBe((await resolvedColor(zing.chrome, 'var(--card-ink)')).color)
	await zing.close()
})

test('the tab list still scrolls with the active card merged', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate((url) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'add-tabs', count: 60, url }), `${base}/many-`)
	await expect(rows(zing.chrome)).toHaveCount(61)
	const last = (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.at(-1)!.id
	await zing.chrome.evaluate((id) => window.zing.activateTab(id), last)
	const list = zing.chrome.locator('.rail-tabs')
	await list.hover()
	await zing.chrome.mouse.wheel(0, 400)
	await expect.poll(() => list.evaluate((el) => (el as unknown as { scrollTop: number }).scrollTop)).toBeGreaterThan(0)
	expect(await list.evaluate((el) => {
		const node = el as unknown as { scrollWidth: number; clientWidth: number }
		return node.scrollWidth - node.clientWidth
	})).toBe(0)
	await zing.close()
})

test('switching tabs doesn\'t move the others', async () => {
	const zing = await launchZing()
	for (const title of ['Alpha', 'Beta', 'Gamma', 'Delta']) await openPage(zing.chrome, title)
	await expect(rows(zing.chrome)).toHaveCount(5)
	const before = await rowLooks(zing.chrome)
	await rows(zing.chrome).nth(1).click({ position: { x: 20, y: 15 } })
	await expect(rows(zing.chrome).nth(1)).toHaveAttribute('aria-selected', 'true')
	const after = await rowLooks(zing.chrome)
	for (const i of [0, 2, 3]) expect(after[i]!.top).toBe(before[i]!.top)
	await zing.close()
})

test('on the right the active card merges leftward', async () => {
	const zing = await launchZing()
	for (const title of ['Alpha', 'Beta', 'Gamma']) await openPage(zing.chrome, title)
	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'right'))
	await expect(zing.chrome.getByRole('navigation', { name: 'Tab rail' })).toBeVisible()
	await rows(zing.chrome).nth(2).click({ position: { x: 20, y: 15 } })
	await expect(rows(zing.chrome).nth(2)).toHaveAttribute('aria-selected', 'true')
	const page = (await shownPage(zing.chrome, 'Beta'))!
	const active = await rows(zing.chrome).nth(2).evaluate((el) => {
		const box = (el as unknown as { getBoundingClientRect(): { left: number } }).getBoundingClientRect()
		const style = (globalThis as unknown as { getComputedStyle(e: unknown): { borderRadius: string } }).getComputedStyle(el)
		return { left: box.left, radius: style.borderRadius }
	})
	expect(active.left).toBe(page.x + page.width)
	expect(active.radius).toBe('0px 10px 10px 0px')
	await zing.close()
})

test('collapsed, cards are icon chips', async () => {
	const zing = await launchZing()
	for (const title of ['Alpha', 'Beta', 'Gamma']) await openPage(zing.chrome, title)
	await zing.chrome.getByRole('button', { name: 'Collapse the tab sidebar to icons' }).click()
	await expect.poll(async () => (await shownPage(zing.chrome, 'Gamma'))?.x).toBe(48)
	await rows(zing.chrome).nth(2).click({ position: { x: 18, y: 16 } })
	await expect(rows(zing.chrome).nth(2)).toHaveAttribute('aria-selected', 'true')
	const chips = await rows(zing.chrome).evaluateAll((els) => {
		const dom = globalThis as unknown as { getComputedStyle(e: unknown): { backgroundColor: string; borderRadius: string } }
		const nodes = els as unknown as { getAttribute(name: string): string | null; getBoundingClientRect(): { top: number; width: number; height: number } }[]
		return nodes.map((el) => {
			const box = el.getBoundingClientRect()
			const style = dom.getComputedStyle(el)
			return { active: el.getAttribute('aria-selected') === 'true', top: box.top, width: box.width, height: box.height, background: style.backgroundColor, radius: style.borderRadius }
		})
	})
	for (const chip of chips) {
		expect(chip.width).toBe(36)
		expect(chip.height).toBe(32)
	}
	for (let i = 0; i + 1 < chips.length; i++) expect(chips[i + 1]!.top - chips[i]!.top).toBe(36)
	for (let i = 0; i < chips.length; i++) {
		const chip = chips[i]!
		if (chip.active) {
			expect(chip.background).toBe((await resolvedColor(zing.chrome, 'var(--page)')).background)
			expect(chip.radius).toBe('8px')
		} else {
			expect(chip.background).toBe((await resolvedColor(zing.chrome, 'var(--card)')).background)
		}
	}
	await expect(zing.chrome.getByRole('navigation', { name: 'Tab rail' }).locator('.rail-row-title').first()).toBeHidden()
	await zing.close()
})

test('the floating rail’s active card stays inside it', async () => {
	const zing = await launchZing()
	for (const title of ['Alpha', 'Beta', 'Gamma']) await openPage(zing.chrome, title)
	// Unpinning from Settings peeks the sidebar out over the page, as the floating rail.
	await zing.chrome.evaluate(() => window.zing.setSetting('sidebarPinned', false))
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	const floating = surface.locator('.rail.floating')
	await expect(floating).toBeVisible()
	const active = floating.locator('.rail-tab.active')
	const looks = await active.evaluate((el) => {
		const dom = globalThis as unknown as { getComputedStyle(e: unknown): { borderRadius: string } }
		const railEl = (el as unknown as { closest(s: string): { getBoundingClientRect(): { right: number } } | null }).closest('.rail')!
		const box = (el as unknown as { getBoundingClientRect(): { right: number } }).getBoundingClientRect()
		return { right: box.right, railRight: railEl.getBoundingClientRect().right, radius: dom.getComputedStyle(el).borderRadius }
	})
	expect(looks.radius).toBe('8px')
	expect(looks.right).toBeLessThanOrEqual(looks.railRight)
	await zing.close()
})

test('on the left the active card merges rightward, as before', async () => {
	const zing = await launchZing()
	for (const title of ['Alpha', 'Beta', 'Gamma']) await openPage(zing.chrome, title)
	await rows(zing.chrome).nth(2).click({ position: { x: 20, y: 15 } })
	await expect(rows(zing.chrome).nth(2)).toHaveAttribute('aria-selected', 'true')
	const page = (await shownPage(zing.chrome, 'Beta'))!
	const active = await rows(zing.chrome).nth(2).evaluate((el) => {
		const box = (el as unknown as { getBoundingClientRect(): { right: number } }).getBoundingClientRect()
		const style = (globalThis as unknown as { getComputedStyle(e: unknown): { borderRadius: string; marginRight: string } }).getComputedStyle(el)
		return { right: box.right, radius: style.borderRadius, marginRight: style.marginRight }
	})
	expect(active.right).toBe(page.x)
	expect(active.radius).toBe('10px 0px 0px 10px')
	expect(active.marginRight).toBe('-8px')
	await zing.close()
})

test('on the right, a collapsed active chip is a plain 8px chip with no merge', async () => {
	const zing = await launchZing()
	for (const title of ['Alpha', 'Beta', 'Gamma']) await openPage(zing.chrome, title)
	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'right'))
	await zing.chrome.getByRole('button', { name: 'Collapse the tab sidebar to icons' }).click()
	await rows(zing.chrome).nth(2).click({ position: { x: 18, y: 16 } })
	await expect(rows(zing.chrome).nth(2)).toHaveAttribute('aria-selected', 'true')
	const chip = await rows(zing.chrome).nth(2).evaluate((el) => {
		const box = (el as unknown as { getBoundingClientRect(): { width: number; height: number } }).getBoundingClientRect()
		const style = (globalThis as unknown as { getComputedStyle(e: unknown): { borderRadius: string; marginLeft: string; marginRight: string } }).getComputedStyle(el)
		return { width: box.width, height: box.height, radius: style.borderRadius, marginLeft: style.marginLeft, marginRight: style.marginRight }
	})
	expect(chip.width).toBe(36)
	expect(chip.height).toBe(32)
	expect(chip.radius).toBe('8px')
	expect(chip.marginLeft).toBe('0px')
	expect(chip.marginRight).toBe('0px')
	await zing.close()
})

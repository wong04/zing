// Phase 5: every view writes the theme's colours as CSS variables, the overlay and page-UI views included.
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { DEFAULT_CUSTOM_COLOR } from '../src/shared/settings'
import { accentLabel, accentThemes } from '../src/shared/theme/accent'
import { accentVariables } from '../src/shared/theme/accentVariables'
import { launchZing, test, pageWhere } from './zing'
import { expectAccent, viewAccent, type ViewGlobals } from './theme-helpers'

declare const window: { readonly zing: ZingApi }

let server: Server
let base = ''
let port = 0

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const title = decodeURIComponent((req.url ?? '/').slice(1)) || 'Home'
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>${title}</title><p>needle one</p><p>needle two</p>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	port = (server.address() as AddressInfo).port
	base = `http://127.0.0.1:${port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

/** A computed style of the first element matching `selector` in this view, as the browser serialises it. */
async function styleOf(view: Page, selector: string, property: string): Promise<string> {
	return view.evaluate(
		([sel, prop]) => {
			const g = globalThis as unknown as ViewGlobals
			return g.getComputedStyle(g.document.querySelector(sel)).getPropertyValue(prop)
		},
		[selector, property] as const,
	)
}

/** A #rrggbb colour as getComputedStyle reports it. */
function rgb(hex: string): string {
	const n = parseInt(hex.slice(1), 16)
	return `rgb(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255})`
}

/** Sets a colour input as the colour popup does when it closes: the value, then `change`. The popup itself is never opened. */
async function pickColour(field: Locator, hex: string): Promise<void> {
	await field.evaluate((element: unknown, value: string) => {
		const g = globalThis as unknown as { Event: new (type: string) => unknown }
		const input = element as { value: string; dispatchEvent(event: unknown): boolean }
		input.value = value
		input.dispatchEvent(new g.Event('change'))
	}, hex)
}

/** Opens a tab on a page with this title on `host.localhost` and waits until the strip shows it. */
async function openTab(chrome: Page, host: string, title: string): Promise<void> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `http://${host}.localhost:${port}/${title}`])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.title).toBe(title)
}

test('every view takes the theme', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/find` } })
	await zing.chrome.evaluate(() => window.zing.setSetting('accent', 'blue'))
	await expectAccent(zing.chrome, 'blue')

	await zing.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(sheet.getByRole('dialog', { name: 'Settings' })).toBeVisible()
	await expectAccent(sheet, 'blue')
	await sheet.getByRole('button', { name: 'Close settings (Esc)' }).click()

	await zing.chrome.evaluate(() => window.zing.runCommand('page.find'))
	const find = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(find.getByRole('textbox', { name: 'Find in page' })).toBeVisible()
	await expectAccent(find, 'blue')
	await zing.chrome.evaluate(() => window.zing.closeFind())

	await zing.chrome.evaluate(() => window.zing.runCommand('tabs.overview'))
	const overview = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay-overview'))
	await expectAccent(overview, 'blue')
	await zing.chrome.evaluate(() => window.zing.closeOverview())

	await zing.chrome.evaluate(async () => {
		const { activeId } = await window.zing.getTabs()
		await window.zing.navigateTab(activeId!, 'ftp://example.com/file')
	})
	const notice = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(notice.getByText("Zing can't open ftp://example.com/file")).toBeVisible()
	await expectAccent(notice, 'blue')
	await zing.close()
})

test('the stacked-tab dropdown takes the theme', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate(() => window.zing.setSetting('accent', 'blue'))
	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
	await expect(zing.chrome.locator('.tabstrip')).toBeVisible()
	// Two hosts on one registrable domain stack together.
	await openTab(zing.chrome, 'a.site', 'Inbox')
	await openTab(zing.chrome, 'b.site', 'Docs')
	await zing.chrome.evaluate(() => window.zing.setTabDropdowns(true))
	await zing.chrome.locator('.tab.stack').click()
	const dropdown = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay-stack'))
	await expect(dropdown.locator('.stack-row')).toHaveCount(2)
	await expectAccent(dropdown, 'blue')
	await zing.close()
})

test('the toolbar and Settings carry the theme', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate(() => window.zing.setSetting('accent', 'blue'))
	await expectAccent(zing.chrome, 'blue')
	const chromeVars = accentVariables('blue', DEFAULT_CUSTOM_COLOR, (await viewAccent(zing.chrome)).dark)
	expect(await styleOf(zing.chrome, '.toolbar', 'background-color')).toBe(rgb(chromeVars['--band']!))
	expect(await styleOf(zing.chrome, '.address', 'background-color')).toBe(rgb(chromeVars['--field']!))

	await zing.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(sheet.getByRole('dialog', { name: 'Settings' })).toBeVisible()
	const sheetVars = accentVariables('blue', DEFAULT_CUSTOM_COLOR, (await viewAccent(sheet)).dark)
	expect(await styleOf(sheet, '.sheet-nav', 'background-color')).toBe(rgb(sheetVars['--band']!))
	expect(await styleOf(sheet, '.sheet-nav-title', 'color')).toBe(rgb(sheetVars['--band-ink']!))
	expect(await styleOf(sheet, '.nav-row:not(.selected)', 'color')).toBe(rgb(sheetVars['--band-ink']!))

	const picker = sheet.getByRole('radiogroup', { name: 'Theme' })
	await expect(picker.getByRole('radio')).toHaveCount(accentThemes.length + 1)
	for (const theme of accentThemes) await expect(picker.getByRole('radio', { name: accentLabel(theme), exact: true })).toHaveCount(1)
	await zing.close()
})

test('the app sidebar takes the theme', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate(() => {
		void window.zing.setSetting('tabBarPosition', 'right')
		void window.zing.setSetting('accent', 'blue')
	})
	await expectAccent(zing.chrome, 'blue')
	await zing.chrome.locator('.sidebar-slot > .sidebar').waitFor()
	const vars = accentVariables('blue', DEFAULT_CUSTOM_COLOR, (await viewAccent(zing.chrome)).dark)
	expect(await styleOf(zing.chrome, '.sidebar-slot > .sidebar', 'background-color')).toBe(rgb(vars['--window']!))
	expect(await styleOf(zing.chrome, '.side-row:not(.selected)', 'color')).toBe(rgb(vars['--window-ink']!))
	expect(await styleOf(zing.chrome, '.side-row.selected', 'background-color')).toBe(rgb(vars['--page']!))
	expect(await styleOf(zing.chrome, '.side-row.selected', 'color')).toBe(rgb(vars['--page-ink']!))
	await zing.close()
})

test('the custom accent applies in the chrome view without an error', async () => {
	const zing = await launchZing()
	const errors: string[] = []
	zing.chrome.on('pageerror', (error) => errors.push(error.message))
	await zing.chrome.evaluate(() => window.zing.setSetting('accent', 'custom'))
	await zing.chrome.evaluate(() => window.zing.setSetting('customColor', '#3366CC'))
	await expectAccent(zing.chrome, 'custom', '#3366CC')
	expect(errors).toEqual([])
	await zing.close()
})

test('arrow keys move through the themes, Custom included', async () => {
	const zing = await launchZing({ env: { ZING_E2E_OFFLINE: '1' } })
	await zing.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	const custom = sheet.getByRole('radio', { name: 'Custom', exact: true })
	const amber = sheet.getByRole('radio', { name: 'Amber', exact: true })
	await amber.focus()
	await sheet.keyboard.press('ArrowLeft')
	await expect(custom).toHaveAttribute('aria-checked', 'true')
	await expect(custom).toBeFocused()
	await sheet.keyboard.press('ArrowRight')
	await expect(amber).toHaveAttribute('aria-checked', 'true')
	await expect(amber).toBeFocused()
	await zing.close()
})

test('a custom colour themes everything', async () => {
	const zing = await launchZing({ env: { ZING_E2E_OFFLINE: '1' } })
	await zing.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(sheet.getByRole('dialog', { name: 'Settings' })).toBeVisible()
	const custom = sheet.getByRole('radio', { name: 'Custom', exact: true })
	const amber = sheet.getByRole('radio', { name: 'Amber', exact: true })
	const field = sheet.getByLabel('Custom colour')
	await expect(field).toHaveCount(0)

	await custom.click()
	await expect(custom).toHaveAttribute('aria-checked', 'true')
	await expect(custom).toHaveClass(/\bselected\b/)
	await expect(amber).toHaveAttribute('aria-checked', 'false')
	await expect(amber).not.toHaveClass(/\bselected\b/)
	await expect(field).toBeVisible()

	await pickColour(field, '#3366CC')
	await expect.poll(() => zing.chrome.evaluate(() => window.zing.getSettings())).toMatchObject({ accent: 'custom', customColor: '#3366cc' })
	expect(await styleOf(sheet, '.swatches > .swatch:last-child > span', 'background-color')).toBe(rgb('#3366CC'))
	await expectAccent(zing.chrome, 'custom', '#3366CC')
	await expectAccent(sheet, 'custom', '#3366CC')
	const start = await pageWhere(zing.browser, (p) => p.url() === 'zing://home' || p.url() === 'zing://home/')
	await expectAccent(start, 'custom', '#3366CC')
	await zing.close()

	const again = await launchZing({ env: { ZING_E2E_OFFLINE: '1' }, userDataDir: zing.userDataDir })
	await expect.poll(() => again.chrome.evaluate(() => window.zing.getSettings())).toMatchObject({ accent: 'custom', customColor: '#3366cc' })
	await expectAccent(again.chrome, 'custom', '#3366CC')
	await again.close()
})

/** A rail element's box and paint, as the view computes them. */
interface RailPaint {
	readonly title: string | null
	readonly height: string
	readonly margin: string
	readonly borderWidth: string
	readonly boxShadow: string
	readonly clipPath: string
}

async function railPaint(elements: Locator): Promise<RailPaint[]> {
	return elements.evaluateAll((els) => {
		const dom = globalThis as unknown as {
			getComputedStyle(e: unknown): {
				height: string
				marginTop: string
				marginRight: string
				marginBottom: string
				marginLeft: string
				borderTopWidth: string
				boxShadow: string
				clipPath: string
			}
		}
		const nodes = els as unknown as { getAttribute(name: string): string | null }[]
		return nodes.map((el) => {
			const style = dom.getComputedStyle(el)
			return {
				title: el.getAttribute('title'),
				height: style.height,
				margin: [style.marginTop, style.marginRight, style.marginBottom, style.marginLeft].join(' '),
				borderWidth: style.borderTopWidth,
				boxShadow: style.boxShadow,
				clipPath: style.clipPath,
			}
		})
	})
}

test('in a contrast theme cards are plain outlined rows', async () => {
	const zing = await launchZing()
	await openTab(zing.chrome, 'a.site', 'Alpha')
	await openTab(zing.chrome, 'b.site', 'Beta')
	// Beta is a favourite, so it hangs as a ribbon and leaves the card list.
	await zing.chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
	await zing.chrome.emulateMedia({ forcedColors: 'active' })

	for (const side of ['left', 'right'] as const) {
		await zing.chrome.evaluate((position) => window.zing.setSetting('tabBarPosition', position), side)
		await expect(zing.chrome.locator(`.rail.pinned.${side}`)).toBeVisible()

		const cards = await railPaint(zing.chrome.locator('.rail-tabs [role="tab"]'))
		expect(cards.map((card) => card.title)).toContain('Alpha')
		for (const card of cards) {
			expect(card).toMatchObject({ height: '32px', margin: '0px 0px 0px 0px', borderWidth: '1px', boxShadow: 'none' })
		}

		const [ribbon] = await railPaint(zing.chrome.locator('.rail-favourites > .rail-ribbon:not(.empty)'))
		expect(ribbon).toMatchObject({ height: '32px', clipPath: 'none', borderWidth: '1px', boxShadow: 'none' })

		const focused = zing.chrome.locator('.rail-favourites > .rail-ribbon:not(.empty)').first()
		await focused.focus()
		await zing.chrome.keyboard.press('Shift+Tab')
		await zing.chrome.keyboard.press('Tab')
		await expect(focused).toBeFocused()
		await expect(focused).toHaveCSS('outline-style', 'solid')
		await expect(focused).toHaveCSS('outline-width', '2px')
	}
	await zing.close()
})

/** What a page keeps through a theme switch: its scroll, when its document was made, and its window.name. */
async function pageKeeps(view: Page): Promise<{ scrollY: number; timeOrigin: number; name: string }> {
	return view.evaluate(() => {
		const g = globalThis as unknown as { scrollY: number; name: string; performance: { timeOrigin: number } }
		return { scrollY: g.scrollY, timeOrigin: g.performance.timeOrigin, name: g.name }
	})
}

test('letters take the ink of the card or ribbon they sit on', async () => {
	const zing = await launchZing()
	const chrome = zing.chrome
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}/Mail`])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.title).toBe('Mail')
	const card = chrome.locator(`.rail-tabs [data-tab-id="${activeId}"]`)
	const cardInk = await card.evaluate((el: unknown) => (globalThis as unknown as ViewGlobals).getComputedStyle(el).getPropertyValue('color'))
	await expect(card.locator('.tab-icon')).toHaveCSS('color', cardInk)

	await chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
	await chrome.evaluate((id) => window.zing.closeTab(id), activeId!)

	const ribbon = chrome.locator('.rail-favourites > .rail-ribbon.unloaded')
	await expect(ribbon).toHaveCount(1)
	const letter = ribbon.locator('.rail-letter')
	await expect(letter).toHaveCSS('opacity', '1')
	const ink = await styleOf(chrome, ':root', '--ribbon-ink')
	await expect(letter).toHaveCSS('color', rgb(ink.trim()))
	await zing.close()
})

test('the Tab colours setting paints cards and ribbons in the theme colour or none', async () => {
	const zing = await launchZing()
	const chrome = zing.chrome
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}/Mail`])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.title).toBe('Mail')
	await chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))

	const ribbon = chrome.locator('.rail-favourites > .rail-ribbon').first()
	const card = chrome.locator('.rail-tabs .rail-tab:not(.active)').first()
	const paintOf = async (variable: string) => rgb((await styleOf(chrome, ':root', variable)).trim())
	await expect(ribbon).toHaveCSS('background-color', await paintOf('--colour-ribbon'))
	await expect(card).toHaveCSS('background-color', await paintOf('--colour-card'))

	await chrome.evaluate(() => window.zing.setSetting('tabColours', 'none'))
	await expect(ribbon).toHaveCSS('background-color', await paintOf('--plain'))
	await expect(ribbon).toHaveCSS('color', await paintOf('--plain-ink'))
	await expect(card).toHaveCSS('background-color', await paintOf('--plain'))
	await expect(card).toHaveCSS('color', await paintOf('--plain-ink'))
	await zing.close()
})

test('switching theme keeps everything', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/Long` } })
	const long = await pageWhere(zing.browser, (p) => p.url() === `${base}/Long`)
	await long.evaluate(() => {
		const g = globalThis as unknown as {
			document: { body: { insertAdjacentHTML(position: string, html: string): void } }
			scrollTo(x: number, y: number): void
			name: string
		}
		g.document.body.insertAdjacentHTML('beforeend', '<p>filler</p>'.repeat(400))
		g.name = 'before-switch'
		g.scrollTo(0, 800)
	})
	await expect.poll(async () => (await pageKeeps(long)).scrollY).toBe(800)

	const { activeId: longId } = await zing.chrome.evaluate(() => window.zing.getTabs())
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const start = await pageWhere(zing.browser, (p) => p.url() === 'zing://home' || p.url() === 'zing://home/')
	const startKeeps = await pageKeeps(start)
	const startAccent = (await viewAccent(start)).accent
	await zing.chrome.evaluate((id) => window.zing.activateTab(id!), longId)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId).toBe(longId)
	const tabCount = (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.length
	const before = await pageKeeps(long)

	await zing.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(sheet.getByRole('dialog', { name: 'Settings' })).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.runCommand('page.find'))
	const find = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(find.getByRole('textbox', { name: 'Find in page' })).toBeVisible()

	for (const theme of accentThemes) {
		await zing.chrome.evaluate((accent) => window.zing.setSetting('accent', accent), theme)
		for (const view of [zing.chrome, sheet, find, start]) await expectAccent(view, theme)
	}

	expect(await pageKeeps(long)).toEqual(before)
	expect(await pageKeeps(start)).toEqual(startKeeps)
	expect((await viewAccent(start)).accent).not.toBe(startAccent)
	await expect(sheet.getByRole('dialog', { name: 'Settings' })).toBeVisible()
	await expect(find.getByRole('textbox', { name: 'Find in page' })).toBeVisible()
	expect((await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.length).toBe(tabCount)
	await zing.close()
})

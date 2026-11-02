// Phase 5: every view writes the theme's colours as CSS variables, the overlay and page-UI views included.
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { DEFAULT_CUSTOM_COLOR } from '../src/shared/settings'
import { accentLabel, accentThemes } from '../src/shared/theme/accent'
import { accentVariables } from '../src/shared/theme/accentVariables'
import { launchZing, test, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

/** The globals a view's page has, typed here because this spec does not compile against the DOM lib. */
interface ViewGlobals {
	document: { documentElement: unknown; querySelector(selector: string): unknown }
	getComputedStyle(element: unknown): { getPropertyValue(name: string): string }
	matchMedia(query: string): { matches: boolean }
}

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

/** The view's own --accent, and whether the view is in its dark appearance (which the palette depends on). */
async function viewAccent(view: Page): Promise<{ accent: string; dark: boolean }> {
	return view.evaluate(() => {
		const g = globalThis as unknown as ViewGlobals
		return {
			accent: g.getComputedStyle(g.document.documentElement).getPropertyValue('--accent').trim(),
			dark: g.matchMedia('(prefers-color-scheme: dark)').matches,
		}
	})
}

/** Waits until the view shows the accent that `accent` and `customColor` give in its own appearance. */
async function expectAccent(view: Page, accent: string, customColor = DEFAULT_CUSTOM_COLOR): Promise<void> {
	const { dark } = await viewAccent(view)
	await expect.poll(async () => (await viewAccent(view)).accent).toBe(accentVariables(accent, customColor, dark)['--accent'])
}

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

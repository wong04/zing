// Phase 5: every view writes the theme's colours as CSS variables, the overlay and page-UI views included.
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { DEFAULT_CUSTOM_COLOR } from '../src/shared/settings'
import { accentVariables } from '../src/shared/theme/accentVariables'
import { launchZing, test, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

/** The globals a view's page has, typed here because this spec does not compile against the DOM lib. */
interface ViewGlobals {
	document: { documentElement: unknown }
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

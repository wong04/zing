import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pages, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>Fixture</title><p>${req.url}</p>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const isSurface = (url: string) => url.endsWith('#surface')

async function openToolbox(zing: Launched): Promise<Page> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => isSurface(p.url()))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	// The sheet draws before the host gives its view the keyboard, and a key sent in between is lost.
	await expect(surface.getByRole('searchbox', { name: 'Search tools' })).toBeFocused()
	return surface
}

test('the Toolbox button opens the sheet, not a notice', async () => {
	const zing = await launchZing()
	const surface = await openToolbox(zing)
	const overlay = pages(zing.browser).find((p) => p.url().endsWith('#overlay'))
	if (overlay) await expect(overlay.getByText("isn't implemented")).toHaveCount(0)
	await surface.keyboard.press('Escape')
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toHaveCount(0)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getShell())).toolboxOpen).toBe(false)
	await zing.close()
})

test('search finds a tool by keyword and Enter opens it', async () => {
	const zing = await launchZing()
	const surface = await openToolbox(zing)
	await surface.getByRole('searchbox', { name: 'Search tools' }).fill('sha')
	await expect(surface.getByRole('button', { name: 'Hashes' }).first()).toBeVisible()
	await expect(surface.getByTestId('toolbox-result').first()).toHaveText(/Hashes/)
	await surface.keyboard.press('Enter')
	await expect(surface.getByTestId('float-panel').filter({ has: surface.getByText('Hashes', { exact: true }) })).toBeVisible()
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toHaveCount(0)
	await zing.close()
})

test('Escape closes the sheet after a click on a blank part of it', async () => {
	const zing = await launchZing()
	const surface = await openToolbox(zing)
	await surface.getByRole('heading', { name: 'Toolbox' }).click()
	await surface.keyboard.press('Escape')
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toHaveCount(0)
	await zing.close()
})

test('Enter on Close, reached by Tab from a search, closes the sheet and opens no tool', async () => {
	const zing = await launchZing()
	const surface = await openToolbox(zing)
	await surface.getByRole('searchbox', { name: 'Search tools' }).fill('sha')
	await expect(surface.getByTestId('toolbox-result').first()).toHaveText(/Hashes/)
	await surface.keyboard.press('Tab')
	await expect(surface.getByRole('button', { name: /Close Toolbox/ })).toBeFocused()
	await surface.keyboard.press('Enter')
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toHaveCount(0)
	expect((await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels).toHaveLength(0)
	await zing.close()
})

test('a tool opened from a search has its first field focused', async () => {
	const zing = await launchZing()
	const surface = await openToolbox(zing)
	await surface.getByRole('searchbox', { name: 'Search tools' }).fill('sha')
	await expect(surface.getByTestId('toolbox-result').first()).toHaveText(/Hashes/)
	await surface.keyboard.press('Enter')
	await expect(surface.getByRole('textbox', { name: 'Text to hash' })).toBeFocused()
	await zing.close()
})

test('opening a tool records it in recents', async () => {
	const zing = await launchZing()
	const surface = await openToolbox(zing)
	await surface.getByTestId('toolbox-tile').filter({ hasText: 'Hashes' }).click()
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getSettings())).recentFeatures).toContain('tool:Hashes')
	await zing.close()
})

test('opening the Toolbox makes no network request', async () => {
	const zing = await launchZing()
	// The surface view is created when the sheet first opens, so watch every page from the moment it appears.
	const asked: { readonly url: string; readonly surface: boolean }[] = []
	const watch = (page: Page): void => {
		page.on('request', (request) => asked.push({ url: request.url(), surface: isSurface(page.url()) || isSurface(request.url()) }))
	}
	pages(zing.browser).forEach(watch)
	for (const context of zing.browser.contexts()) context.on('page', watch)
	const surface = await openToolbox(zing)
	await surface.getByTestId('toolbox-tile').first().waitFor()
	const fromSurface = asked.filter((r) => r.surface)
	expect(fromSurface.length).toBeGreaterThan(0)
	expect(fromSurface.filter((r) => new URL(r.url).hostname !== 'app.zing')).toEqual([])
	await zing.close()
})

async function openLinks(zing: Launched): Promise<Page> {
	const surface = await openToolbox(zing)
	await surface.getByTestId('toolbox-tile').filter({ hasText: 'Links' }).click()
	await expect(surface.getByRole('dialog', { name: 'Links' })).toBeVisible()
	return surface
}

test('the default list holds Desmos, Figma, Notion, ChatGPT, Claude, Gemini', async () => {
	const zing = await launchZing()
	const surface = await openLinks(zing)
	await expect(surface.getByTestId('link-tile')).toHaveText(['Desmos', 'Figma', 'Notion', 'ChatGPT', 'Claude', 'Gemini'])
	await expect(surface.getByText('Sites here sign you out when Zing closes, unless you turn on Stay signed in to sites in Settings.')).toBeVisible()
	await zing.close()
})

test('a link tile opens a tab, and an added link survives a relaunch', async () => {
	const first = await launchZing()
	const surface = await openLinks(first)
	await surface.getByRole('textbox', { name: 'Link name' }).fill('Local fixture')
	await surface.getByRole('textbox', { name: 'Link address' }).fill(`${base}/linked`)
	await surface.getByRole('button', { name: 'Add link' }).click()
	await surface.getByTestId('link-tile').filter({ hasText: 'Local fixture' }).click()
	await expect
		.poll(async () => (await first.chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.url))
		.toContain(`${base}/linked`)
	const { userDataDir } = first
	await first.close()

	const second = await launchZing({ userDataDir })
	const again = await openLinks(second)
	await expect(again.getByTestId('link-tile').filter({ hasText: 'Local fixture' })).toBeVisible()
	await again.getByRole('button', { name: 'Remove Local fixture' }).click()
	await expect(again.getByTestId('link-tile').filter({ hasText: 'Local fixture' })).toHaveCount(0)
	await second.close()
})

test('an address that is not a web link is refused with a message', async () => {
	const zing = await launchZing()
	const surface = await openLinks(zing)
	await surface.getByRole('textbox', { name: 'Link address' }).fill('javascript:alert(1)')
	await surface.getByRole('button', { name: 'Add link' }).click()
	await expect(surface.getByRole('alert')).toContainText('not a link')
	await expect(surface.getByTestId('link-tile')).toHaveCount(6)
	await zing.close()
})

test('a stored link that is not http or https shows an error and opens nothing', async () => {
	const zing = await launchZing()
	// settings.json can be edited by hand, so the tile checks the address again when it is clicked.
	await zing.chrome.evaluate(() => window.zing.setSetting('toolLinks', [{ title: 'Bad', url: 'javascript://x/%0aalert(1)' }]))
	const surface = await openLinks(zing)
	await surface.getByTestId('link-tile').filter({ hasText: 'Bad' }).click()
	await expect(surface.getByRole('alert')).toContainText('not a web link')
	expect((await zing.chrome.evaluate(() => window.zing.getTabs())).tabs).toHaveLength(1)
	await zing.close()
})

test('a link with an AI tag loses it on navigation', async () => {
	const zing = await launchZing()
	await expect(zing.chrome.locator('[role="tab"]')).toHaveCount(1)
	const { activeId } = await zing.chrome.evaluate(() => window.zing.getTabs())
	await zing.chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId, `${base}/tagged?utm_source=chatgpt.com`])
	await expect
		.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.url)
		.toBe(`${base}/tagged`)
	await zing.close()
})

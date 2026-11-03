import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pages, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const isSurface = (url: string) => url.endsWith('#surface')

async function openToolbox(zing: Launched): Promise<Page> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => isSurface(p.url()))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
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

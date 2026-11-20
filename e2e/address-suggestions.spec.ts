// Address-bar suggestions: open tabs, bookmarks and the Reading List, never history, ranked by the host
// (addressbar:suggest) and drawn by Zing's own view (#suggest) under the field, which keeps the keyboard.
import { writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { FAVOURITES_ID, PINNED_ID } from '../src/shared/library/library'
import { launchZing, layers, pageWhere, scratchDir, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		const title = { '/alpha': 'Alpha page', '/beta': 'Beta page', '/alpha-docs': 'Docs', '/later': 'Later' }[req.url ?? '']
		if (!title) {
			res.statusCode = 404
			return res.end()
		}
		res.end(`<!doctype html><title>${title}</title><p>${title}</p>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

/** A profile with one bookmark and one Reading List item that match "alpha". */
function profile(): string {
	const dir = scratchDir('zing-suggest-')
	const library = {
		bookmarks: [
			{ id: FAVOURITES_ID, title: 'Favourites', updatedAt: 1, children: [{ id: 'b1', title: 'Alpha bookmark', url: `${base}/alpha-docs`, updatedAt: 1 }] },
			{ id: PINNED_ID, title: 'Pinned', updatedAt: 1, children: [] },
		],
		readingList: [{ id: 'r1', title: 'Alpha reading', url: `${base}/later`, added: 1, isRead: false, updatedAt: 1 }],
	}
	writeFileSync(join(dir, 'library.json'), JSON.stringify(library))
	return dir
}

/** Two tabs: Alpha behind, Beta in front. */
async function twoTabs(): Promise<{ zing: Launched; alphaId: string }> {
	const zing = await launchZing({ userDataDir: profile(), env: { ZING_START_URL: `${base}/alpha` } })
	await expect(zing.chrome.locator('[role="tab"]')).toContainText('Alpha page')
	const alphaId = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId!
	await zing.chrome.evaluate((url) => window.zing.openLinkInNewTab(url), `${base}/beta`)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId).not.toBe(alphaId)
	await expect(zing.chrome.locator('[role="tab"]')).toHaveCount(2)
	return { zing, alphaId }
}

const address = (zing: Launched) => zing.chrome.getByRole('textbox', { name: 'Address and search bar' })
// The bridge leaves out a null value, so a closed list arrives as undefined.
const suggestions = async (zing: Launched) => (await zing.chrome.evaluate(() => window.zing.getAddressSuggestions())) ?? null
const suggestLayer = async (zing: Launched) => (await layers(zing.chrome)).find((l) => l.url.endsWith('#suggest'))
const suggestView = (zing: Launched): Promise<Page> => pageWhere(zing.browser, (p) => p.url().endsWith('#suggest'))

test('typing ranks open tabs, bookmarks and the Reading List under the field, without taking the keyboard', async () => {
	const { zing, alphaId } = await twoTabs()
	await address(zing).click()
	await address(zing).fill('alpha')

	await expect.poll(async () => (await suggestions(zing))?.items.map((i) => i.kind)).toEqual(['tab', 'bookmark', 'reading'])
	const state = (await suggestions(zing))!
	expect(state.items[0]).toEqual({ kind: 'tab', title: 'Alpha page', url: `${base}/alpha`, tabId: alphaId })
	expect(state.active).toBe(-1)

	const view = await suggestView(zing)
	await expect(view.getByRole('option')).toHaveCount(3)
	await expect(view.getByRole('option').first()).toContainText('Switch to tab')
	await expect.poll(async () => (await suggestLayer(zing))?.visible).toBe(true)
	const layer = (await suggestLayer(zing))!
	const field = (await address(zing).boundingBox())!
	expect(layer.y).toBeGreaterThanOrEqual(field.y + field.height)
	expect(Math.abs(layer.x - field.x)).toBeLessThan(2)
	expect(layer.keyboard).toBe(false)
	await expect(address(zing)).toBeFocused()

	// Nothing matches: the list goes.
	await address(zing).fill('zzzz')
	await expect.poll(() => suggestions(zing)).toBeNull()
	await expect.poll(async () => (await suggestLayer(zing))?.visible).toBe(false)
	await zing.close()
})

test('the arrows move the highlight and Enter on an open tab switches to it', async () => {
	const { zing, alphaId } = await twoTabs()
	await address(zing).click()
	await address(zing).fill('alpha')
	await expect.poll(async () => (await suggestions(zing))?.items.length).toBe(3)
	const view = await suggestView(zing)

	await address(zing).press('ArrowDown')
	await expect.poll(async () => (await suggestions(zing))?.active).toBe(0)
	await address(zing).press('ArrowDown')
	await expect.poll(async () => (await suggestions(zing))?.active).toBe(1)
	await expect(view.getByRole('option', { selected: true })).toContainText('Alpha bookmark')
	await address(zing).press('ArrowUp')
	await expect.poll(async () => (await suggestions(zing))?.active).toBe(0)

	await address(zing).press('Enter')
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId).toBe(alphaId)
	await expect(zing.chrome.locator('[role="tab"]')).toHaveCount(2)
	await expect.poll(() => suggestions(zing)).toBeNull()
	await expect.poll(async () => (await suggestLayer(zing))?.visible).toBe(false)
	await zing.close()
})

test('Enter on a bookmark opens it in the tab on screen', async () => {
	const { zing } = await twoTabs()
	const beta = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId
	await address(zing).click()
	await address(zing).fill('alpha')
	await expect.poll(async () => (await suggestions(zing))?.items.length).toBe(3)
	await address(zing).press('ArrowDown')
	await address(zing).press('ArrowDown')
	await expect.poll(async () => (await suggestions(zing))?.active).toBe(1)
	await address(zing).press('Enter')
	await pageWhere(zing.browser, (p) => p.url() === `${base}/alpha-docs`)
	const tabs = await zing.chrome.evaluate(() => window.zing.getTabs())
	expect(tabs.activeId).toBe(beta)
	expect(tabs.tabs.find((t) => t.id === beta)?.url).toBe(`${base}/alpha-docs`)
	await zing.close()
})

test('Esc closes the list and keeps what was typed; a second Esc gives it up', async () => {
	const { zing } = await twoTabs()
	await address(zing).click()
	await address(zing).fill('alpha')
	await expect.poll(async () => (await suggestions(zing))?.items.length).toBe(3)
	await address(zing).press('Escape')
	await expect.poll(() => suggestions(zing)).toBeNull()
	await expect.poll(async () => (await suggestLayer(zing))?.visible).toBe(false)
	await expect(address(zing)).toHaveValue('alpha')
	await address(zing).press('Escape')
	await expect(address(zing)).not.toHaveValue('alpha')
	await zing.close()
})

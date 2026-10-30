import { mkdtempSync, readFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, test, type ElectronApplication, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing } from './zing'

// Only ever evaluated inside the chrome renderer, where the preload defines it.
declare const window: { zing: ZingApi }

let server: Server
let origin: string

test.beforeAll(async () => {
	server = createServer((request, response) => {
		const name = (request.url ?? '/').replace(/^\//, '') || 'index'
		response.setHeader('content-type', 'text/html')
		response.end(`<!doctype html><title>Fixture ${name}</title><h1>${name}</h1>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => server.close())

function scratchLibrary(): string {
	return join(mkdtempSync(join(tmpdir(), 'zing-e2e-')), 'library.json')
}

async function launchWithLibrary(file: string): Promise<{ app: ElectronApplication; chrome: Page }> {
	process.env['ZING_LIBRARY_FILE'] = file
	try {
		return await launchZing()
	} finally {
		delete process.env['ZING_LIBRARY_FILE']
	}
}

async function visit(chrome: Page, path: string): Promise<void> {
	await chrome.evaluate(async (url) => {
		const { activeId } = await window.zing.getTabs()
		if (activeId) await window.zing.navigateTab(activeId, url)
	}, `${origin}/${path}`)
	await expect
		.poll(() => chrome.evaluate(async () => {
			const { tabs, activeId } = await window.zing.getTabs()
			return tabs.find((t) => t.id === activeId)?.title
		}))
		.toBe(`Fixture ${path}`)
}

async function run(chrome: Page, command: 'library.addFavourite' | 'library.addReadingList' | 'tabs.overview'): Promise<void> {
	await chrome.evaluate((id) => window.zing.runCommand(id), command)
}

test('a bookmarked page is on the Favourites bar and survives a relaunch', async () => {
	const file = scratchLibrary()
	const first = await launchWithLibrary(file)
	await visit(first.chrome, 'saved')
	await run(first.chrome, 'library.addFavourite')
	await expect(first.chrome.locator('.favourites-bar .fav-title')).toHaveText('Fixture saved')
	await first.app.close()

	const saved = JSON.parse(readFileSync(file, 'utf8')) as { bookmarks: { children: { url: string }[] }[] }
	expect(saved.bookmarks[0]?.children[0]?.url).toBe(`${origin}/saved`)

	const second = await launchWithLibrary(file)
	await expect(second.chrome.locator('.favourites-bar .fav-title')).toHaveText('Fixture saved')
	await second.app.close()
})

test('the side panel lists the bookmark and reading list item', async () => {
	const { app, chrome } = await launchWithLibrary(scratchLibrary())
	await visit(chrome, 'readme')
	await run(chrome, 'library.addFavourite')
	await run(chrome, 'library.addReadingList')
	await chrome.evaluate(() => window.zing.runCommand('library.bookmarks'))
	await expect(chrome.locator('.library-panel .library-row', { hasText: 'Fixture readme' })).toBeVisible()
	await chrome.evaluate(() => window.zing.runCommand('library.readingList'))
	await expect(chrome.locator('.library-panel .library-row')).toHaveCount(1)
	await app.close()
})

test('Ctrl-clicking a Favourites folder opens every link in it', async () => {
	const { app, chrome } = await launchWithLibrary(scratchLibrary())
	await visit(chrome, 'one')
	await run(chrome, 'library.addFavourite')
	await visit(chrome, 'two')
	await run(chrome, 'library.addFavourite')
	await chrome.evaluate(async () => {
		const { library } = await window.zing.getLibrary()
		const fav = library.bookmarks[0] as { children: { id: string }[] }
		await window.zing.applyLibrary({ type: 'newFolder', ids: fav.children.map((c) => c.id) })
	})
	const folder = chrome.locator('.fav-chip.folder')
	await expect(folder).toHaveCount(1)
	await folder.click({ modifiers: ['Control'] })
	await expect(chrome.locator('[role="tab"]')).toHaveCount(3)
	await app.close()
})

async function findPage(app: ElectronApplication, matches: (url: string) => boolean): Promise<Page> {
	const existing = app.windows().find((p) => matches(p.url()))
	if (existing) return existing
	return app.waitForEvent('window', { predicate: (p) => matches(p.url()) })
}

test('the start page shows its tiles and its search goes through the address rule', async () => {
	const { app, chrome } = await launchWithLibrary(scratchLibrary())
	// Nothing in these tests may reach a live site.
	await app.evaluate(({ session }) =>
		session.fromPartition('zing-private').setProxy({ proxyRules: 'http://127.0.0.1:9', proxyBypassRules: '<local>' }),
	)
	const start = await findPage(app, (url) => url === 'zing://home' || url === 'zing://home/')
	await expect(start.locator('.tile')).toHaveCount(8)
	await start.locator('input[name=q]').fill('hello world')
	await start.locator('input[name=q]').press('Enter')
	await expect(chrome.locator('.address')).toHaveValue(/q=hello%20world/)
	await app.close()
})

test('the tab overview shows a card per tab', async () => {
	const { app, chrome } = await launchWithLibrary(scratchLibrary())
	await visit(chrome, 'first')
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	await expect(chrome.locator('[role="tab"]')).toHaveCount(2)
	await expect
		.poll(() => chrome.evaluate(async () => (await window.zing.getTabs()).tabs.some((t) => t.loading)))
		.toBe(false)
	await run(chrome, 'tabs.overview')
	const overlay = await findPage(app, (url) => url.endsWith('#overlay-overview'))
	await expect(overlay.locator('.overview-card')).toHaveCount(2)
	await expect(overlay.locator('.overview-thumb img')).not.toHaveCount(0)
	await overlay.keyboard.press('Escape')
	await expect(overlay.locator('.overview-card')).toHaveCount(0)
	await app.close()
})

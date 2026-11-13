// A person closes Zing and clicks it again straight away. With many pages open, the old Zing is still quitting when
// the new one starts, so the new one must neither hand itself to the dying Zing (and be lost), nor read the profile
// before the old one has written it.
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

const LOADED_TABS = 12

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>page ${req.url}</title><p>${'text '.repeat(2000)}</p>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

for (const delay of [0, 300]) {
	test(`a relaunch ${delay} ms after closing a Zing with ${LOADED_TABS} loaded tabs opens a window with everything saved`, async () => {
		const first = await launchZing()
		await first.chrome.evaluate(() => window.zing.setSetting('restoreTabs', true))
		const urls: string[] = []
		for (let n = 0; n < LOADED_TABS; n++) {
			const url = `${base}/page-${n}`
			await first.chrome.evaluate(() => window.zing.runCommand('tab.new'))
			const { activeId } = await first.chrome.evaluate(() => window.zing.getTabs())
			await first.chrome.evaluate(([id, to]) => window.zing.navigateTab(id!, to!), [activeId!, url])
			await pageWhere(first.browser, (p) => p.url() === url)
			urls.push(url)
		}
		// Changed just before quitting, inside the stores' debounce, so only the flush on quit can save them.
		const folder = await first.chrome.evaluate(() => window.zing.applyLibrary({ type: 'newFolder', ids: [] }))
		await first.chrome.evaluate((id) => window.zing.applyLibrary({ type: 'rename', id: id!, title: 'saved before quit' }), folder)
		await first.chrome.evaluate(() => window.zing.setSetting('searchEngine', 'duckduckgo'))

		const quitting = first.quit()
		await new Promise((resolve) => setTimeout(resolve, delay))
		const second = await launchZing({ userDataDir: first.userDataDir })
		await quitting

		await expect.poll(async () => (await second.chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.url)).toEqual(expect.arrayContaining(urls))
		const { library } = await second.chrome.evaluate(() => window.zing.getLibrary())
		expect(library.bookmarks.some((node) => node.title === 'saved before quit')).toBe(true)
		expect((await second.chrome.evaluate(() => window.zing.getSettings())).searchEngine).toBe('duckduckgo')
		await second.close()
	})
}

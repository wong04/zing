import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, test, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

const SET = `<script>
	localStorage.setItem('k', 'v')
	document.cookie = 'k=v; path=/'
	document.title = 'set:' + localStorage.getItem('k') + ':' + document.cookie
</script>`
const READ = `<script>document.title = JSON.stringify({ ls: localStorage.getItem('k'), cookie: document.cookie })</script>`

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>…</title>${req.url?.startsWith('/set') ? SET : READ}`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

async function openIn(chrome: Page, id: string, path: string): Promise<void> {
	await chrome.evaluate(([tab, url]) => window.zing.navigateTab(tab!, url!), [id, `${base}${path}`])
}

async function newTab(chrome: Page): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	return (await chrome.evaluate(() => window.zing.getTabs())).activeId!
}

const titleOf = async (chrome: Page, id: string) => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === id)?.title

test('a site keeps nothing once its last tab closes', async () => {
	const zing = await launchZing()
	const first = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId!
	await openIn(zing.chrome, first, '/set')
	await expect.poll(() => titleOf(zing.chrome, first)).toBe('set:v:k=v')

	// A second tab on the same site keeps its data alive when the first one closes.
	const second = await newTab(zing.chrome)
	await openIn(zing.chrome, second, '/read')
	await expect.poll(() => titleOf(zing.chrome, second)).toBe(JSON.stringify({ ls: 'v', cookie: 'k=v' }))
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), first)

	const third = await newTab(zing.chrome)
	await openIn(zing.chrome, third, '/read?still')
	await expect.poll(() => titleOf(zing.chrome, third)).toBe(JSON.stringify({ ls: 'v', cookie: 'k=v' }))

	// Now close every tab on the site: the start page is left, and the site is purged.
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), second)
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), third)
	const again = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId!
	await pageWhere(zing.browser, (p) => p.url().startsWith('zing://home'))
	await expect
		.poll(async () => {
			await openIn(zing.chrome, again, `/read?${Date.now()}`)
			await new Promise((resolve) => setTimeout(resolve, 300))
			return titleOf(zing.chrome, again)
		}, { timeout: 10_000 })
		.toBe(JSON.stringify({ ls: null, cookie: '' }))
	await zing.close()
})

// blockTrackers (PARITY B11): a third-party request to a listed tracker domain is refused and tracking parameters are
// stripped while it is on; turned off, open tabs load both. The tracker names are mapped to the local server.
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test } from './zing'

declare const window: { readonly zing: ZingApi }

const GIF = Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64')

let server: Server
let port = 0
const pixelHosts: string[] = []
const pageQueries: string[] = []

/**
 * A page that asks for a pixel on `pixelHost`, as an image and as a no-cors fetch, and titles itself with both outcomes and
 * its own query, so each load's title is its own. Zing refuses a tracker with an answer (403), so the fetch is "answered";
 * Edge's own tracking prevention fails the request instead, so "failed" would mean Zing's rule never saw it.
 */
const page = (pixelHost: string) => `<!doctype html><title>waiting</title>
<script>
	const src = 'http://${pixelHost}:${port}/pixel?' + Date.now()
	const image = new Promise((resolve) => {
		const img = new Image()
		img.onload = () => resolve('loaded')
		img.onerror = () => resolve('refused')
		img.src = src
	})
	const fetched = fetch(src + '-fetch', { mode: 'no-cors', cache: 'no-store' }).then(() => 'answered', () => 'failed')
	Promise.all([image, fetched]).then(([a, b]) => { document.title = a + '/' + b + location.search })
</script>`

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const url = new URL(req.url ?? '/', `http://${req.headers.host}`)
		res.setHeader('cache-control', 'no-store')
		if (url.pathname === '/pixel') {
			pixelHosts.push(url.hostname)
			res.setHeader('content-type', 'image/gif')
			res.end(GIF)
			return
		}
		if (url.pathname === '/page') pageQueries.push(url.search)
		res.setHeader('content-type', 'text/html')
		res.end(page(url.pathname === '/own' ? 'static.hotjar.com' : 'pixel.doubleclick.net'))
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	port = (server.address() as AddressInfo).port
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const tabOf = async (chrome: Page, id: string) => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === id)

async function open(chrome: Page, id: string, url: string): Promise<void> {
	await chrome.evaluate(([tab, to]) => window.zing.navigateTab(tab!, to!), [id, url])
}

test('blockTrackers refuses third-party trackers and strips parameters, and turned off allows both', async () => {
	const zing = await launchZing({ env: { ZING_E2E_MAP_HOSTS: 'MAP *.doubleclick.net 127.0.0.1 , MAP *.hotjar.com 127.0.0.1' } })
	const id = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId!
	const base = `http://127.0.0.1:${port}`

	// On (the default): the tracker's pixel never reaches the server, and the address loses its tracker.
	await open(zing.chrome, id, `${base}/page?utm_source=x&id=1`)
	await expect.poll(async () => (await tabOf(zing.chrome, id))?.title).toBe('refused/answered?id=1')
	expect((await tabOf(zing.chrome, id))?.url).toBe(`${base}/page?id=1`)
	expect(pageQueries).toEqual(['?id=1'])
	expect(pixelHosts).toEqual([])

	// A tracker's own site still loads its own resources: only third-party requests are refused.
	await open(zing.chrome, id, `http://www.hotjar.com:${port}/own`)
	await expect.poll(async () => (await tabOf(zing.chrome, id))?.title).toBe('loaded/answered')
	expect(pixelHosts).toEqual(['static.hotjar.com', 'static.hotjar.com'])

	// Off: the open tab's next load asks for the pixel and keeps its parameters.
	await zing.chrome.evaluate(() => window.zing.setSetting('blockTrackers', false))
	await open(zing.chrome, id, `${base}/page?utm_source=x&id=2`)
	await expect.poll(async () => (await tabOf(zing.chrome, id))?.title).toBe('loaded/answered?utm_source=x&id=2')
	expect((await tabOf(zing.chrome, id))?.url).toBe(`${base}/page?utm_source=x&id=2`)
	expect(pageQueries.at(-1)).toBe('?utm_source=x&id=2')
	expect(pixelHosts).toEqual(['static.hotjar.com', 'static.hotjar.com', 'pixel.doubleclick.net', 'pixel.doubleclick.net'])

	// On again, for the same open tab.
	await zing.chrome.evaluate(() => window.zing.setSetting('blockTrackers', true))
	await open(zing.chrome, id, `${base}/page?utm_source=x&id=3`)
	await expect.poll(async () => (await tabOf(zing.chrome, id))?.title).toBe('refused/answered?id=3')
	expect(pageQueries.at(-1)).toBe('?id=3')
	expect(pixelHosts).toEqual(['static.hotjar.com', 'static.hotjar.com', 'pixel.doubleclick.net', 'pixel.doubleclick.net'])
	await zing.close()
})

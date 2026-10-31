// Phase W0 checklist, driven over CDP the way Zing's e2e suite will be.
// Run: npx playwright test -c spikes/webview2/playwright.config.ts
import { spawn, spawnSync, type ChildProcess } from 'node:child_process'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { chromium, expect, test, type Browser, type Page } from '@playwright/test'

const EXE = join(import.meta.dirname, 'bin', 'Release', 'net9.0-windows10.0.22621.0', 'win-x64', 'ZingSpike.exe')

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const send = (html: string) => {
			res.setHeader('content-type', 'text/html')
			res.end(`<!doctype html>${html}`)
		}
		if (req.url === '/opener') return send(`<title>/opener</title><script>addEventListener('message', (e) => { document.title = 'got:' + e.data })</script>`)
		if (req.url === '/popup') return send(`<title>popup</title><script>window.opener.postMessage('hello-from-popup', '*'); window.close()</script>`)
		if (req.url === '/store') return send(`<title>store</title>`)
		res.statusCode = 404
		res.end()
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

interface Spike {
	process: ChildProcess
	browser: Browser
}

let nextPort = 9400

async function launch(env: Record<string, string> = {}): Promise<Spike> {
	const port = String(nextPort++)
	const process_ = spawn(EXE, [], { env: { ...process.env, ZING_E2E: '1', ZING_SPIKE_CDP: port, ...env }, stdio: 'ignore' })
	await expect
		.poll(async () => (await fetch(`http://127.0.0.1:${port}/json/version`).catch(() => undefined))?.ok ?? false, { timeout: 30_000 })
		.toBe(true)
	const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`)
	return { process: process_, browser }
}

async function close(spike: Spike): Promise<void> {
	await spike.browser.close().catch(() => undefined)
	spike.process.kill()
}

function pages(spike: Spike): Page[] {
	return spike.browser.contexts().flatMap((c) => c.pages())
}

async function pageWhere(spike: Spike, match: (p: Page) => boolean): Promise<Page> {
	await expect.poll(() => pages(spike).some(match), { timeout: 15_000 }).toBe(true)
	return pages(spike).find(match)!
}

test('Playwright attaches over CDP and zing://home is served by the host', async () => {
	const spike = await launch()
	const home = await pageWhere(spike, (p) => p.url().startsWith('zing://home'))
	await expect(home.locator('h1')).toHaveText('zing://home works')
	await close(spike)
})

test('a popup keeps window.opener, delivers its message and closes', async () => {
	const spike = await launch({ ZING_SPIKE_URL: `${base}/opener` })
	const opener = await pageWhere(spike, (p) => p.url() === `${base}/opener`)
	await opener.evaluate(() => void window.open('/popup'))
	await expect.poll(() => opener.title()).toBe('got:hello-from-popup')
	await expect.poll(() => pages(spike).filter((p) => p.url().endsWith('/popup')).length).toBe(0)
	await close(spike)
})

test('the overlay is a separate transparent page above the content', async () => {
	const spike = await launch()
	const overlay = await pageWhere(spike, (p) => p.url().startsWith('data:') || p.url() === 'about:blank' || /zing-overlay/.test(p.url()))
	await expect.poll(() => overlay.title()).toBe('zing-overlay')
	expect(await overlay.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe('rgba(0, 0, 0, 0)')
	await close(spike)
})

test('one site’s data can be purged without touching another (CDP Storage.clearDataForOrigin)', async () => {
	const spike = await launch({ ZING_SPIKE_URL: `${base}/store` })
	const page = await pageWhere(spike, (p) => p.url() === `${base}/store`)
	await page.evaluate(() => {
		localStorage.setItem('k', 'v')
		document.cookie = 'c=1; path=/'
	})
	const cdp = await page.context().newCDPSession(page)
	await cdp.send('Storage.clearDataForOrigin', { origin: base, storageTypes: 'all' })
	await page.reload()
	expect(await page.evaluate(() => [localStorage.getItem('k'), document.cookie])).toEqual([null, ''])
	await close(spike)
})

/** Working set of the spike and every WebView2 process it started, in MB. */
function memoryMb(pid: number): number {
	const script = `$ids = @(${pid}); $all = Get-CimInstance Win32_Process; do { $more = $all | Where-Object { $ids -contains $_.ParentProcessId -and $ids -notcontains $_.ProcessId } | ForEach-Object ProcessId; $ids += $more } while ($more); ($all | Where-Object { $ids -contains $_.ProcessId } | Measure-Object WorkingSetSize -Sum).Sum`
	const out = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { encoding: 'utf8', windowsHide: true })
	return Math.round(Number(out.stdout.trim()) / 1024 / 1024)
}

test('memory: one tab vs thirty more', async () => {
	const one = await launch()
	await pageWhere(one, (p) => p.url().startsWith('zing://home'))
	await new Promise((r) => setTimeout(r, 3000))
	const oneMb = memoryMb(one.process.pid!)
	await close(one)

	const many = await launch({ ZING_SPIKE_TABS: '30' })
	await expect.poll(() => pages(many).filter((p) => p.url().startsWith('zing://home')).length, { timeout: 60_000 }).toBeGreaterThanOrEqual(31)
	await new Promise((r) => setTimeout(r, 3000))
	const manyMb = memoryMb(many.process.pid!)
	await close(many)

	console.info(`memory: 1 tab ${oneMb} MB, 31 tabs ${manyMb} MB (${Math.round((manyMb - oneMb) / 30)} MB per extra tab)`)
	expect(oneMb).toBeGreaterThan(0)
})

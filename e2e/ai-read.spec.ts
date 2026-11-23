import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import { PDFDocument, StandardFonts } from 'pdf-lib'
import type { ZingApi } from '../src/shared/ipc'
import { connectMcp, shareTab, textOf } from './mcp'
import { launchZing, pageWhere, test } from './zing'

declare const window: { readonly zing: ZingApi }

// The AI reading tools over MCP: list_tabs, snapshot, read_text and screenshot, through Zing's own HTTP endpoint.

let server: Server
let base = ''
let pdf: Uint8Array

const PAGES: Record<string, string> = {
	'/plain': '<!doctype html><title>plain</title><h1>Plain page</h1><p>Nothing to see.</p>',
	'/other': '<!doctype html><title>other</title><p>Another page</p>',
	'/login': '<!doctype html><title>login</title><form><label>Email <input id="user" name="user"></label><label>Password <input id="pw" type="password" name="pw"></label><button>Sign in</button></form>',
}

test.beforeAll(async () => {
	const doc = await PDFDocument.create()
	const font = await doc.embedFont(StandardFonts.Helvetica)
	doc.addPage([400, 200]).drawText('Quarterly Zebra Report', { x: 20, y: 150, size: 18, font })
	pdf = await doc.save()
	server = createServer((req, res) => {
		if (req.url === '/doc.pdf') {
			res.setHeader('content-type', 'application/pdf')
			res.end(Buffer.from(pdf))
			return
		}
		res.setHeader('content-type', 'text/html')
		res.end(PAGES[req.url!] ?? '<!doctype html><title>missing</title>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

async function openTab(chrome: Page, path: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}${path}`])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.url).toBe(`${base}${path}`)
	return activeId!
}

async function onlyTab(mcp: Awaited<ReturnType<typeof connectMcp>>): Promise<string> {
	// The share message is posted, so it lands a moment later.
	let tabs: { tab: string }[] = []
	await expect.poll(async () => (tabs = JSON.parse(textOf(await mcp.call('list_tabs'))) as { tab: string }[]).length).toBe(1)
	return tabs[0]!.tab
}

test('list_tabs shows only shared tabs', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const shared = await openTab(zing.chrome, '/plain')
	await openTab(zing.chrome, '/other')
	await shareTab(zing.chrome, shared)
	await expect.poll(async () => JSON.parse(textOf(await mcp.call('list_tabs'))) as { url: string }[]).toEqual([{ tab: expect.stringMatching(/^[a-z0-9]{12}$/) as unknown, url: `${base}/plain` }])
	await zing.close()
})

test('snapshot of a fixture login page has refs and no password value', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const id = await openTab(zing.chrome, '/login')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/login`)
	await page.fill('#user', 'ada@example.test')
	await page.fill('#pw', 'hunter2-secret')
	await shareTab(zing.chrome, id)
	const tab = await onlyTab(mcp)
	const snapshot = textOf(await mcp.call('snapshot', { tab }))
	expect(snapshot).toMatch(/textbox "Email" \[e\d+\] value="ada@example.test"/)
	expect(snapshot).toMatch(/textbox "Password" \[e\d+\]/)
	expect(snapshot).toMatch(/button "Sign in" \[e\d+\]/)
	expect(snapshot).not.toContain('hunter2-secret')
	await zing.close()
})

test('screenshot returns a PNG', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	await shareTab(zing.chrome, await openTab(zing.chrome, '/plain'))
	const result = await mcp.call('screenshot', { tab: await onlyTab(mcp) })
	expect(result.isError).toBeFalsy()
	expect(result.content[0]?.type).toBe('image')
	expect(result.content[0]?.mimeType).toBe('image/png')
	const png = Buffer.from(result.content[0]!.data!, 'base64')
	expect([...png.subarray(0, 8)]).toEqual([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])
	expect(png.length).toBeGreaterThan(1000)
	await zing.close()
})

test('read_text of a PDF answers Unsupported and leaves nothing selected', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	await shareTab(zing.chrome, await openTab(zing.chrome, '/doc.pdf'))
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/doc.pdf`)
	// The viewer's own scripting answer; null until it has loaded the file.
	const selected = () => page.evaluate(() => new Promise<string | null>((resolve) => {
		const embed = document.querySelector('embed') as unknown as { postMessage(m: unknown): void } | null
		if (!embed) return resolve(null)
		const heard = (event: MessageEvent) => {
			if ((event.data as { type?: string } | null)?.type !== 'getSelectedTextReply') return
			globalThis.removeEventListener('message', heard)
			resolve(String((event.data as { selectedText: string }).selectedText))
		}
		globalThis.addEventListener('message', heard)
		embed.postMessage({ type: 'getSelectedText' })
		setTimeout(() => resolve(null), 2000)
	}))
	await expect.poll(selected).toBe('')
	const result = await mcp.call('read_text', { tab: await onlyTab(mcp) })
	expect(result.structuredContent?.code).toBe('Unsupported')
	expect(await selected()).toBe('')
	await zing.close()
})

test('a screenshot of the front tab of a minimised window returns NotVisible', async () => {
	const zing = await launchZing({ env: { ZING_E2E_AI_SHOW_WAIT_MS: '1500' } })
	const mcp = await connectMcp(zing.chrome)
	await shareTab(zing.chrome, await openTab(zing.chrome, '/plain'))
	const tab = await onlyTab(mcp)
	// Started first: a page that starts while its window is minimised fails to take focus (a separate host issue).
	await expect(await pageWhere(zing.browser, (p) => p.url() === `${base}/plain`)).toHaveTitle('plain')
	await zing.chrome.evaluate(() => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'minimise-window', minimised: true }))
	// A call made before the window has gone down may still capture or time out, so it is asked again.
	await expect.poll(async () => (await mcp.call('screenshot', { tab })).structuredContent?.code, { timeout: 45_000 }).toBe('NotVisible')
	await zing.chrome.evaluate(() => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'minimise-window', minimised: false }))
	await zing.close()
})

test('read_text of a page is its text and leaves out what was typed', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const id = await openTab(zing.chrome, '/login')
	await (await pageWhere(zing.browser, (p) => p.url() === `${base}/login`)).fill('#user', 'ada@example.test')
	await shareTab(zing.chrome, id)
	const text = textOf(await mcp.call('read_text', { tab: await onlyTab(mcp) }))
	expect(text).toContain('Sign in')
	expect(text).not.toContain('ada@example.test')
	await zing.close()
})

test('a tab closed mid-call returns TabGone and records no error', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const hidden = await openTab(zing.chrome, '/plain')
	await openTab(zing.chrome, '/other')
	await shareTab(zing.chrome, hidden)
	// A hidden tab's screenshot waits for the owner to show it; the tab closes meanwhile.
	const call = mcp.call('screenshot', { tab: await onlyTab(mcp) })
	await new Promise((resolve) => setTimeout(resolve, 1000))
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), hidden)
	const result = await call
	expect(result.isError).toBe(true)
	expect(result.structuredContent?.code).toBe('TabGone')
	await zing.close()
})

test('a screenshot of a hidden tab that is never shown returns NotVisible and leaves it in the background', async () => {
	const zing = await launchZing({ env: { ZING_E2E_AI_SHOW_WAIT_MS: '1500' } })
	const mcp = await connectMcp(zing.chrome)
	const hidden = await openTab(zing.chrome, '/plain')
	const front = await openTab(zing.chrome, '/other')
	await shareTab(zing.chrome, hidden)
	const result = await mcp.call('screenshot', { tab: await onlyTab(mcp) })
	expect(result.structuredContent?.code).toBe('NotVisible')
	expect((await zing.chrome.evaluate(() => window.zing.getTabs())).activeId).toBe(front)
	await zing.close()
})

test('an open alert returns DialogOpen', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const id = await openTab(zing.chrome, '/plain')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/plain`)
	await shareTab(zing.chrome, id)
	const tab = await onlyTab(mcp)
	// Left open: a handled dialog that is never answered keeps the page's script blocked.
	page.on('dialog', () => {})
	await page.evaluate(() => void setTimeout(() => (globalThis as unknown as { alert(m: string): void }).alert('wait'), 0))
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByRole('alertdialog')).toContainText('wait')
	expect((await mcp.call('snapshot', { tab })).structuredContent?.code).toBe('DialogOpen')
	await zing.close()
})

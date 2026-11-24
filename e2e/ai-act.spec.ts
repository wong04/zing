import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { inflateSync } from 'node:zlib'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { answerBar, confirmBar, connectMcp, shareTab, textOf, type McpClient } from './mcp'
import { launchZing, pageWhere, scratchDir, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

// The AI action tools over MCP: type, click and the form POST backstop, with the confirm bar answered through TestHooks.

const FIXTURES = join(import.meta.dirname, 'fixtures', 'ai-act')

/** A one-page PDF the engine would show in its viewer. */
const PDF = `%PDF-1.4
1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj
2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj
3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj
trailer<</Root 1 0 R>>
%%EOF
`

let server: Server
let base = ''
/** The bodies of the form POSTs the server was sent, in order. */
const posts: string[] = []

test.beforeAll(async () => {
	server = createServer((req, res) => {
		if (req.method === 'POST') {
			let body = ''
			req.on('data', (chunk: Buffer) => (body += chunk.toString()))
			req.on('end', () => {
				posts.push(body)
				res.setHeader('content-type', 'text/html')
				res.end('<!doctype html><title>sent</title><p>Sent</p>')
			})
			return
		}
		if (req.url === '/missing.pdf') {
			res.statusCode = 404
			res.setHeader('content-type', 'text/html')
			res.end('<!doctype html><title>Not found</title><p>Not found')
			return
		}
		if (req.url === '/doc.pdf') {
			res.setHeader('content-type', 'application/pdf')
			res.end(PDF)
			return
		}
		res.setHeader('content-type', 'text/html')
		const name = /^\/([a-z-]+)$/.exec(req.url ?? '')?.[1]
		try {
			res.end(readFileSync(join(FIXTURES, `${name}.html`)))
		} catch {
			res.end('<!doctype html><title>missing</title>')
		}
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

test.beforeEach(() => {
	posts.length = 0
})

async function openTab(chrome: Page, path: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}${path}`])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.url).toBe(`${base}${path}`)
	return activeId!
}

async function sharedTab(chrome: Page, mcp: McpClient, path: string): Promise<{ id: string; tab: string }> {
	const id = await openTab(chrome, path)
	await shareTab(chrome, id)
	let tabs: { tab: string }[] = []
	await expect.poll(async () => (tabs = JSON.parse(textOf(await mcp.call('list_tabs'))) as { tab: string }[]).length).toBe(1)
	return { id, tab: tabs[0]!.tab }
}

/** The ref of the first line in the snapshot matching `pattern`, which must hold a `[eN]`; snapshots are taken until the page has it. */
async function refOf(mcp: McpClient, tab: string, pattern: RegExp): Promise<string> {
	let ref: string | undefined
	await expect.poll(async () => {
		const line = textOf(await mcp.call('snapshot', { tab })).split('\n').find((l) => pattern.test(l))
		ref = line ? /\[(e\d+)\]/.exec(line)?.[1] : undefined
		return ref
	}, { message: `a snapshot line matching ${pattern}` }).toBeDefined()
	return ref!
}

async function settle(): Promise<void> {
	await new Promise((resolve) => setTimeout(resolve, 1500))
}

test('type then click submits a React fixture form only after Allow', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { id, tab } = await sharedTab(zing.chrome, mcp, '/react-form')
	// The fixture is a fair test: a script's value set, with its input event, doesn't stick.
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/react-form`)
	await page.evaluate(() => {
		const field = document.querySelector('#name') as unknown as { value: string; dispatchEvent(e: Event): boolean }
		field.value = 'synthetic'
		field.dispatchEvent(new Event('input', { bubbles: true }))
	})
	await expect(page.locator('#name')).toHaveValue('')
	expect(textOf(await mcp.call('type', { tab, ref: await refOf(mcp, tab, /textbox "Name"/), text: 'Ada Lovelace' }))).toBe('Done')
	await expect(page.locator('#name')).toHaveValue('Ada Lovelace')
	const click = mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Send"/) })
	await expect.poll(() => confirmBar(zing.chrome, id), { timeout: 15_000 }).toContain('Send')
	expect(posts).toEqual([])
	await answerBar(zing.chrome, id, 'allow')
	expect(textOf(await click)).toBe('Done')
	await expect.poll(() => posts).toEqual(['name=Ada+Lovelace'])
	await zing.close()
})

test('Deny leaves the form unsent', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { id, tab } = await sharedTab(zing.chrome, mcp, '/react-form')
	await mcp.call('type', { tab, ref: await refOf(mcp, tab, /textbox "Name"/), text: 'Ada' })
	const click = mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Send"/) })
	await answerBar(zing.chrome, id, 'deny')
	expect((await click).structuredContent?.code).toBe('Denied')
	await settle()
	expect(posts).toEqual([])
	await zing.close()
})

test('a button under an overlay fails Covered', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { tab } = await sharedTab(zing.chrome, mcp, '/overlay')
	const result = await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Open details"/) })
	expect(result.structuredContent?.code).toBe('Covered')
	expect(textOf(await mcp.call('read_text', { tab }))).not.toContain('clicked')
	await zing.close()
})

test('a button in a shadow root takes the click on its label, and one under an overlay there fails Covered', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { tab } = await sharedTab(zing.chrome, mcp, '/shadow')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/shadow`)
	expect((await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Show more"/) })).structuredContent?.code).toBe('Covered')
	expect(textOf(await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Open details"/) }))).toBe('Done')
	await expect(page).toHaveTitle('clicked')
	await zing.close()
})

test("a mislabelled buy button's form POST still asks", async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { id, tab } = await sharedTab(zing.chrome, mcp, '/mislabelled')
	// "Close" is no submit word, so the click itself runs at once; the POST it starts is held.
	expect(textOf(await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Close"/) }))).toBe('Done')
	await answerBar(zing.chrome, id, 'deny')
	await settle()
	expect(posts).toEqual([])
	expect(textOf(await mcp.call('list_tabs'))).toContain(`${base}/mislabelled`)

	expect(textOf(await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Close"/) }))).toBe('Done')
	await answerBar(zing.chrome, id, 'allow')
	await expect.poll(() => posts).toEqual(['item=television'])
	await zing.close()
})

test("a Don't ask site submits without a bar", async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { id, tab } = await sharedTab(zing.chrome, mcp, '/react-form')
	await mcp.call('type', { tab, ref: await refOf(mcp, tab, /textbox "Name"/), text: 'first' })
	const first = mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Send"/) })
	await answerBar(zing.chrome, id, 'always')
	expect(textOf(await first)).toBe('Done')
	await expect.poll(() => posts).toEqual(['name=first'])

	expect(textOf(await mcp.call('navigate', { tab, url: `${base}/react-form` }))).toBe('Done')
	await expect.poll(async () => textOf(await mcp.call('read_text', { tab }))).toContain('Send')
	await mcp.call('type', { tab, ref: await refOf(mcp, tab, /textbox "Name"/), text: 'second' })
	expect(textOf(await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Send"/) }))).toBe('Done')
	await expect.poll(() => posts).toEqual(['name=first', 'name=second'])
	expect(await confirmBar(zing.chrome, id)).toBeNull()
	await zing.close()
})

test('a transparent overlay inside a container covers both the button under it and the container', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { tab } = await sharedTab(zing.chrome, mcp, '/container')
	expect((await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Open details"/) })).structuredContent?.code).toBe('Covered')
	// The ad is inside the card, but it is a control of its own: a hit on it isn't a hit on the card.
	expect((await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Offer"/) })).structuredContent?.code).toBe('Covered')
	const page = await pageWhere(zing.browser, (p) => p.url().startsWith(`${base}/container`))
	await expect(page).toHaveTitle('container')
	await zing.close()
})

test('a held form POST whose navigation is replaced while the owner decides records no error', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { id, tab } = await sharedTab(zing.chrome, mcp, '/mislabelled')
	expect(textOf(await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Close"/) }))).toBe('Done')
	await expect.poll(() => confirmBar(zing.chrome, id), { timeout: 15_000 }).not.toBeNull()
	// The owner goes elsewhere in the tab, which cancels the held navigation; then answers the bar.
	await zing.chrome.evaluate(([tabId, url]) => window.zing.navigateTab(tabId!, url!), [id, `${base}/overlay`])
	await expect.poll(async () => textOf(await mcp.call('list_tabs'))).toContain(`${base}/overlay`)
	if (await confirmBar(zing.chrome, id)) await confirmBar(zing.chrome, id, 'allow')
	await settle()
	expect(posts).toEqual([])
	await zing.close()
})

test('open_tab opens a shared tab behind the active one', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { id } = await sharedTab(zing.chrome, mcp, '/overlay')
	const opened = textOf(await mcp.call('open_tab', { url: `${base}/react-form` }))
	expect(opened).toMatch(/^[a-z0-9]{12}$/)
	await expect.poll(async () => textOf(await mcp.call('read_text', { tab: opened }))).toContain('Send')
	const { activeId, tabs } = await zing.chrome.evaluate(() => window.zing.getTabs())
	expect(activeId).toBe(id)
	expect(tabs.map((t) => t.url)).toContain(`${base}/react-form`)
	await zing.close()
})

test('a held form POST is never sent when AI control is turned off while the bar is up', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { id, tab } = await sharedTab(zing.chrome, mcp, '/mislabelled')
	expect(textOf(await mcp.call('click', { tab, ref: await refOf(mcp, tab, /button "Close"/) }))).toBe('Done')
	await expect.poll(() => confirmBar(zing.chrome, id), { timeout: 15_000 }).not.toBeNull()
	// Turning AI control off unshares every tab, which denies the held POST and stops pausing the page's requests.
	await zing.chrome.evaluate(() => window.zing.setSetting('aiControlEnabled', false))
	await expect.poll(() => confirmBar(zing.chrome, id)).toBeNull()
	await settle()
	await settle()
	expect(posts).toEqual([])
	await zing.close()
})

test('download of a PDF url downloads instead of navigating', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { tab } = await sharedTab(zing.chrome, mcp, '/overlay')
	expect(textOf(await mcp.call('download', { tab, url: `${base}/doc.pdf` }))).toBe('Done')
	const saved = join(zing.userDataDir, 'downloads', 'doc.pdf')
	await expect.poll(() => existsSync(saved) && readFileSync(saved, 'latin1')).toContain('%PDF-1.4')
	await settle()
	expect(textOf(await mcp.call('list_tabs'))).toContain(`${base}/overlay`)
	expect(textOf(await mcp.call('list_tabs'))).not.toContain('doc.pdf')
	await zing.close()
})

// fill_login: the vault on, with the e2e stand-in for Windows Hello (vault-hello).

const USERNAME = 'ada@example.test'
const PASSWORD = 'correct horse battery'

function vaultProfile(): string {
	const dir = scratchDir('zing-host-data-')
	mkdirSync(dir, { recursive: true })
	writeFileSync(join(dir, 'settings.json'), JSON.stringify({ passwordAutofill: true, restoreTabs: false }))
	return dir
}

const hello = (zing: Launched, result: 'pass' | 'cancel' | 'hold'): Promise<void> =>
	zing.chrome.evaluate((r) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'vault-hello', result: r }), result)

async function saveLogin(zing: Launched, username: string, password: string): Promise<void> {
	await zing.chrome.evaluate((login) => window.zing.saveLogin(login), { origin: `${base}/login`, username, password, title: '', note: '' })
}

async function loginTab(zing: Launched, mcp: McpClient, path = '/login'): Promise<{ id: string; tab: string; page: Page }> {
	const shared = await sharedTab(zing.chrome, mcp, path)
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}${path}`)
	await page.waitForLoadState('load')
	return { ...shared, page }
}

/** The RGBA (or RGB) pixels of an 8-bit, non-interlaced PNG. */
function pixels(png: Buffer): { width: number; height: number; channels: number; data: Uint8Array } {
	const width = png.readUInt32BE(16)
	const height = png.readUInt32BE(20)
	const channels = png[25] === 6 ? 4 : 3
	const idat: Buffer[] = []
	for (let at = 8; at < png.length; ) {
		const length = png.readUInt32BE(at)
		if (png.toString('latin1', at + 4, at + 8) === 'IDAT') idat.push(png.subarray(at + 8, at + 8 + length))
		at += length + 12
	}
	const raw = inflateSync(Buffer.concat(idat))
	const stride = width * channels
	const data = new Uint8Array(height * stride)
	for (let y = 0; y < height; y++) {
		const filter = raw[y * (stride + 1)]!
		for (let x = 0; x < stride; x++) {
			const value = raw[y * (stride + 1) + 1 + x]!
			const a = x >= channels ? data[y * stride + x - channels]! : 0
			const b = y > 0 ? data[(y - 1) * stride + x]! : 0
			const c = x >= channels && y > 0 ? data[(y - 1) * stride + x - channels]! : 0
			const p = a + b - c
			const paeth = Math.abs(p - a) <= Math.abs(p - b) && Math.abs(p - a) <= Math.abs(p - c) ? a : Math.abs(p - b) <= Math.abs(p - c) ? b : c
			const predicted = [0, a, b, (a + b) >> 1, paeth][filter]!
			data[y * stride + x] = (value + predicted) & 0xff
		}
	}
	return { width, height, channels, data }
}

function allBlack(png: Buffer): boolean {
	const { data, channels } = pixels(png)
	for (let i = 0; i < data.length; i += channels) if (data[i]! > 0 || data[i + 1]! > 0 || data[i + 2]! > 0) return false
	return true
}

test('fill_login with one login fills after Hello', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile() })
	await hello(zing, 'pass')
	await saveLogin(zing, USERNAME, PASSWORD)
	const mcp = await connectMcp(zing.chrome)
	const { tab, page } = await loginTab(zing, mcp)

	await hello(zing, 'cancel')
	expect((await mcp.call('fill_login', { tab })).structuredContent?.code).toBe('Denied')
	await expect(page.locator('#pass')).toHaveValue('')

	await hello(zing, 'pass')
	expect(textOf(await mcp.call('fill_login', { tab }))).toBe('Filled')
	await expect(page.locator('#user')).toHaveValue(USERNAME)
	await expect(page.locator('#pass')).toHaveValue(PASSWORD)
	await zing.close()
})

test('fill_login with no login for the site is NoSavedLogin', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile() })
	const mcp = await connectMcp(zing.chrome)
	const { tab } = await loginTab(zing, mcp)
	expect((await mcp.call('fill_login', { tab })).structuredContent?.code).toBe('NoSavedLogin')
	await zing.close()
})

test('two logins ask the owner', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile() })
	await hello(zing, 'pass')
	await saveLogin(zing, USERNAME, PASSWORD)
	await saveLogin(zing, 'bob@example.test', "bob's password")
	const mcp = await connectMcp(zing.chrome)
	const { page } = await loginTab(zing, mcp)
	const tab = JSON.parse(textOf(await mcp.call('list_tabs')))[0].tab as string

	const filling = mcp.call('fill_login', { tab })
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#picker'))
	const option = ui.getByRole('option', { name: 'bob@example.test' })
	await expect(option).toBeVisible()
	await expect(ui.getByRole('option', { name: USERNAME })).toBeVisible()
	// A click counts once the picker has been on screen for 500 ms.
	await ui.waitForTimeout(700)
	await option.click()
	const result = await filling
	expect(textOf(result)).toBe('Filled')
	expect(JSON.stringify(result)).not.toContain('bob')
	await expect(page.locator('#user')).toHaveValue('bob@example.test')
	await expect(page.locator('#pass')).toHaveValue("bob's password")

	// Closing the picker is Denied.
	const again = mcp.call('fill_login', { tab })
	await expect(ui.getByRole('option', { name: USERNAME })).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.closeAccountPicker())
	expect((await again).structuredContent?.code).toBe('Denied')
	await zing.close()
})

test('the response never holds credential text', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile() })
	await hello(zing, 'pass')
	await saveLogin(zing, USERNAME, PASSWORD)
	const mcp = await connectMcp(zing.chrome)
	const { tab, page } = await loginTab(zing, mcp)
	const results = [await mcp.call('fill_login', { tab })]
	await expect(page.locator('#pass')).toHaveValue(PASSWORD)
	results.push(await mcp.call('snapshot', { tab }), await mcp.call('read_text', { tab }), await mcp.call('list_tabs'))
	const all = JSON.stringify(results)
	expect(all).not.toContain(USERNAME)
	expect(all).not.toContain(PASSWORD)
	expect(all).not.toContain('ada')
	await zing.close()
})

test('a show-password toggle after fill still masks snapshot and screenshot', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile() })
	await hello(zing, 'pass')
	await saveLogin(zing, USERNAME, PASSWORD)
	const mcp = await connectMcp(zing.chrome)
	const { tab, page } = await loginTab(zing, mcp)
	expect(textOf(await mcp.call('fill_login', { tab }))).toBe('Filled')
	await page.click('#show')
	await expect(page.locator('#pass')).toHaveAttribute('type', 'text')

	const snapshot = textOf(await mcp.call('snapshot', { tab }))
	expect(snapshot).not.toContain(PASSWORD)
	expect(snapshot).not.toContain(USERNAME)
	const field = await refOf(mcp, tab, /textbox "Password"/)
	const shot = await mcp.call('screenshot', { tab, ref: field })
	expect(allBlack(Buffer.from(shot.content[0]!.data!, 'base64'))).toBe(true)
	// The button beside it isn't masked: the check above would catch a shot that is black for another reason.
	const button = await mcp.call('screenshot', { tab, ref: await refOf(mcp, tab, /button "Show password"/) })
	expect(allBlack(Buffer.from(button.content[0]!.data!, 'base64'))).toBe(false)
	await zing.close()
})

test('a download the server answers 404 fails and leaves the tab on its page', async () => {
	const zing = await launchZing()
	const mcp = await connectMcp(zing.chrome)
	const { tab } = await sharedTab(zing.chrome, mcp, '/overlay')
	const result = await mcp.call('download', { tab, url: `${base}/missing.pdf` })
	expect(result.isError).toBe(true)
	expect(result.content[0]?.text).toContain('404')
	await settle()
	const page = await pageWhere(zing.browser, (p) => p.url().startsWith(base))
	expect(page.url()).toBe(`${base}/overlay`)
	await expect(page).toHaveTitle('overlay')
	expect(textOf(await mcp.call('list_tabs'))).toContain(`${base}/overlay`)
	expect(existsSync(join(zing.userDataDir, 'downloads', 'missing.pdf'))).toBe(false)
	await zing.close()
})

test('fill_login on a page with no sign-in form is Denied, with or without a saved login', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile() })
	await hello(zing, 'pass')
	const mcp = await connectMcp(zing.chrome)
	const { tab } = await loginTab(zing, mcp, '/no-form')
	expect((await mcp.call('fill_login', { tab })).structuredContent?.code).toBe('Denied')
	await saveLogin(zing, USERNAME, PASSWORD)
	expect((await mcp.call('fill_login', { tab })).structuredContent?.code).toBe('Denied')
	await zing.close()
})

test('unsharing the tab while Hello is up fills nothing', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile() })
	await hello(zing, 'pass')
	await saveLogin(zing, USERNAME, PASSWORD)
	const mcp = await connectMcp(zing.chrome)
	const { id, tab, page } = await loginTab(zing, mcp)
	await hello(zing, 'hold')
	const filling = mcp.call('fill_login', { tab })
	await settle()
	await zing.chrome.evaluate((t) => window.zing.stopAi(t), id)
	await hello(zing, 'pass')
	expect((await filling).isError).toBe(true)
	await settle()
	await expect(page.locator('#user')).toHaveValue('')
	await expect(page.locator('#pass')).toHaveValue('')
	await zing.close()
})

test("a page's own value setter never sees the filled login", async () => {
	const zing = await launchZing({ userDataDir: vaultProfile() })
	await hello(zing, 'pass')
	await saveLogin(zing, USERNAME, PASSWORD)
	const mcp = await connectMcp(zing.chrome)
	const { tab, page } = await loginTab(zing, mcp, '/sneaky-login')
	expect(textOf(await mcp.call('fill_login', { tab }))).toBe('Filled')
	await expect(page.locator('#pass')).toHaveValue(PASSWORD)
	await expect(page.locator('#leak')).toHaveText('')
	expect(textOf(await mcp.call('snapshot', { tab }))).not.toContain(PASSWORD)
	await zing.close()
})

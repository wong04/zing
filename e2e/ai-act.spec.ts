import { readFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { answerBar, confirmBar, connectMcp, shareTab, textOf, type McpClient } from './mcp'
import { launchZing, pageWhere, test } from './zing'

declare const window: { readonly zing: ZingApi }

// The AI action tools over MCP: type, click and the form POST backstop, with the confirm bar answered through TestHooks.

const FIXTURES = join(import.meta.dirname, 'fixtures', 'ai-act')

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

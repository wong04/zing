// The password vault's autofill (Task 4): the picker is Zing's own view, drawn under the field and inside the page,
// so the page never sees a saved username; it follows the frame's real origin, never what the page says; a page
// message can only say "a field has focus here" (Ruling 28), so the picker's keys are real key presses the host sees
// (the controller's AcceleratorKeyPressed; CDP input never takes that path, so these tests use the picker-key seam,
// which enters the same handler); a password reaches the page only after a choice and Windows Hello (faked in e2e only,
// through the vault-hello test hook).
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, layers, pageWhere, scratchDir, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const USERNAME = 'ada@example.test'
const PASSWORD = 'correct horse battery'
const OTHER_USERNAME = 'eve@frame.test'

const SIGN_IN = `<form action="/done" method="post">
	<label>Email <input id="user" name="username" autocomplete="username"></label>
	<label>Password <input id="pass" name="password" type="password"></label>
	<button>Sign in</button>
</form>`

let site: Server
let other: Server
let base = ''
let otherBase = ''

function serve(handler: (url: string) => string): Promise<Server> {
	const server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		res.end(handler(req.url ?? '/'))
	})
	return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(server)))
}

const origin = (server: Server): string => `http://127.0.0.1:${(server.address() as AddressInfo).port}`

test.beforeAll(async () => {
	other = await serve(() => `<!doctype html><title>Embedded sign-in</title>${SIGN_IN}`)
	otherBase = origin(other)
	site = await serve((url) => {
		if (url === '/framed') return `<!doctype html><title>Framed</title><input id="top" name="username" autocomplete="username"><iframe id="frame" src="${otherBase}/login" width="400" height="200"></iframe>`
		return `<!doctype html><title>Sign in</title>${SIGN_IN}`
	})
	base = origin(site)
})

test.afterAll(async () => {
	await new Promise<void>((resolve) => site.close(() => resolve()))
	await new Promise<void>((resolve) => other.close(() => resolve()))
})

/** A profile with the vault on or off: passwordAutofill is read at launch, and followed after it. */
function vaultProfile(on: boolean): string {
	const dir = scratchDir('zing-host-data-')
	mkdirSync(dir, { recursive: true })
	writeFileSync(join(dir, 'settings.json'), JSON.stringify({ passwordAutofill: on }))
	return dir
}

type TestMessage = Record<string, unknown>

async function testHook(zing: Launched, message: TestMessage): Promise<void> {
	await zing.chrome.evaluate((m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', ...m }), message)
}

const hello = (zing: Launched, result: 'pass' | 'cancel' | 'unavailable'): Promise<void> => testHook(zing, { action: 'vault-hello', result })

const VK = { ArrowUp: 0x26, ArrowDown: 0x28, Enter: 0x0d, Escape: 0x1b } as const

/** A real key press as the page's controller reports it (AcceleratorKeyPressed), through the same handler. */
const pressKey = (zing: Launched, key: keyof typeof VK): Promise<void> => testHook(zing, { action: 'picker-key', vk: VK[key] })

/** How many page messages the host dropped as malformed, oversized or too deep (Ruling 29). */
async function droppedMessages(zing: Launched): Promise<number> {
	const file = join(scratchDir('zing-dropped-'), 'dropped.json')
	await testHook(zing, { action: 'page-messages', file })
	await expect.poll(() => existsSync(file)).toBe(true)
	return (JSON.parse(readFileSync(file, 'utf8')) as { dropped: number }).dropped
}

async function save(zing: Launched, at: string, username: string, password = PASSWORD): Promise<void> {
	await zing.chrome.evaluate((login) => window.zing.saveLogin(login), { origin: at, username, password, title: '', note: '' })
}

async function open(zing: Launched, url: string): Promise<Page> {
	const tab = await zing.chrome.evaluate(async () => (await window.zing.getTabs()).tabs[0]!.id)
	await zing.chrome.evaluate(([id, to]) => window.zing.navigateTab(id!, to!), [tab, url])
	const page = await pageWhere(zing.browser, (p) => p.url() === url)
	await page.waitForLoadState('load')
	return page
}

/** Posts from the page, as any script on it could. */
async function post(page: Page, message: unknown): Promise<void> {
	await page.evaluate((m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage(m), message)
}

const picker = (zing: Launched): Promise<Page> => pageWhere(zing.browser, (p) => p.url().endsWith('#picker'))

// The bridge leaves out a null value, so a closed picker arrives as undefined.
const pickerState = async (zing: Launched) => (await zing.chrome.evaluate(() => window.zing.getAccountPicker())) ?? null

async function pickerLayer(zing: Launched) {
	return (await layers(zing.chrome)).find((l) => l.url.endsWith('#picker'))
}

/** Long enough for the detection script to report and the host to answer. */
const settle = (page: Page): Promise<void> => page.waitForTimeout(1200)

/** A click counts only once the picker has been on screen for 500 ms (Ruling 28). */
async function choose(ui: Page, username: string): Promise<void> {
	const option = ui.getByRole('option', { name: username })
	await expect(option).toBeVisible()
	await ui.waitForTimeout(700)
	await option.click()
}

test('focusing the username box shows the saved username under it, and choosing it fills both boxes', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, `${base}/login`, USERNAME)
	const page = await open(zing, `${base}/login`)

	await page.click('#user')
	const ui = await picker(zing)
	await expect(ui.getByRole('option', { name: USERNAME })).toBeVisible()
	// It opens with nothing selected, and shows only the username, never a password.
	await expect(ui.getByRole('option', { selected: true })).toHaveCount(0)
	await expect(ui.getByText(PASSWORD)).toHaveCount(0)
	await expect.poll(async () => (await pickerLayer(zing))?.visible).toBe(true)
	const field = await page.locator('#user').boundingBox()
	const layer = (await pickerLayer(zing))!
	const pageLayer = (await layers(zing.chrome)).find((l) => l.url === `${base}/login`)!
	// Under the field, in the window's content: the page's own offset plus the field's bottom.
	expect(layer.y).toBeGreaterThanOrEqual(pageLayer.y + field!.y + field!.height - 1)
	expect(Math.abs(layer.x - (pageLayer.x + field!.x))).toBeLessThan(2)

	// The page never had the username, before any choice.
	expect(await page.evaluate(() => document.documentElement.outerHTML.includes('ada@example.test'))).toBe(false)
	expect(await page.evaluate(() => document.body.innerText.includes('ada@example.test'))).toBe(false)

	await choose(ui, USERNAME)
	await expect(page.locator('#user')).toHaveValue(USERNAME)
	await expect(page.locator('#pass')).toHaveValue(PASSWORD)
	await expect.poll(() => pickerState(zing)).toBeNull()
	await expect.poll(async () => (await pickerLayer(zing))?.visible).toBe(false)
	await zing.close()
})

test('the picker follows real keys: arrows move, Enter chooses, Esc closes; page-relayed keys do nothing', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, `${base}/login`, USERNAME)
	await save(zing, `${base}/login`, 'second@example.test', 'second password')
	const page = await open(zing, `${base}/login`)

	await page.click('#pass')
	const ui = await picker(zing)
	await expect(ui.getByRole('option')).toHaveCount(2)
	const active = async () => (await pickerState(zing))?.active

	// A page can forge a key message; it is not one the host takes, so nothing moves.
	await post(page, { zingCredentialField: { kind: 'key', key: 'down' } })
	await settle(page)
	expect(await active()).toBe(-1)
	// Enter with nothing selected goes to the page as usual: no fill, no Hello, the picker stays.
	await pressKey(zing, 'Enter')
	await settle(page)
	await expect(page.locator('#pass')).toHaveValue('')
	expect(await active()).toBe(-1)

	await pressKey(zing, 'ArrowDown')
	await expect.poll(active).toBe(0)
	await expect(ui.getByRole('option', { selected: true })).toHaveCount(1)
	await pressKey(zing, 'ArrowDown')
	await expect.poll(active).toBe(1)
	await pressKey(zing, 'ArrowUp')
	await expect.poll(active).toBe(0)
	const first = (await pickerState(zing))!.entries[0]!.username
	await pressKey(zing, 'Enter')
	await expect(page.locator('#user')).toHaveValue(first)
	await expect(page.locator('#pass')).toHaveValue(first === USERNAME ? PASSWORD : 'second password')
	await expect.poll(() => pickerState(zing)).toBeNull()

	// Esc closes it without filling.
	await page.locator('#user').fill('')
	await expect.poll(async () => (await pickerState(zing))?.entries.length).toBe(2)
	await pressKey(zing, 'Escape')
	await expect.poll(() => pickerState(zing)).toBeNull()
	await expect(page.locator('#user')).toHaveValue('')
	await zing.close()
})

for (const result of ['cancel', 'unavailable'] as const) {
	test(`with Windows Hello ${result === 'cancel' ? 'cancelled' : 'unavailable'}, choosing fills nothing, shows no error and closes the picker`, async () => {
		const zing = await launchZing({ userDataDir: vaultProfile(true) })
		await save(zing, `${base}/login`, USERNAME)
		const page = await open(zing, `${base}/login`)
		await hello(zing, result)

		await page.click('#user')
		const ui = await picker(zing)
		await choose(ui, USERNAME)

		await expect.poll(() => pickerState(zing)).toBeNull()
		await expect.poll(async () => (await pickerLayer(zing))?.visible).toBe(false)
		await settle(page)
		await expect(page.locator('#user')).toHaveValue('')
		await expect(page.locator('#pass')).toHaveValue('')
		// No notice or error dialog: no notice layer is on screen, and close() fails on any recorded error.
		expect((await layers(zing.chrome)).some((l) => l.url.endsWith('#overlay') && l.visible)).toBe(false)
		await zing.close()
	})
}

test('an iframe from another local port gets no picker, even posting a forged message itself, while the top page still does', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, base, USERNAME)
	await save(zing, otherBase, OTHER_USERNAME)
	const page = await open(zing, `${base}/framed`)
	const frame = page.frameLocator('#frame')

	await frame.locator('#user').click()
	await settle(page)
	expect(await pickerState(zing)).toBeNull()

	// The frame's own script copy does nothing; here the frame posts the message itself.
	const inner = page.frames().find((f) => f.url() === `${otherBase}/login`)!
	const posted = await inner.evaluate(() => {
		const webview = (globalThis as unknown as { chrome?: { webview?: { postMessage(m: unknown): void } } }).chrome?.webview
		webview?.postMessage({ zingCredentialField: { kind: 'username', x: 10, y: 10, w: 200, h: 24 } })
		return webview !== undefined
	})
	console.log('a cross-origin frame has chrome.webview:', posted)
	await settle(page)
	expect(await pickerState(zing)).toBeNull()

	await page.click('#top')
	await expect.poll(async () => (await pickerState(zing))?.entries.map((e) => e.username)).toEqual([USERNAME])
	await zing.close()
})

test('a page posting a forged zingCredentialField message for another origin gets a picker only for its own', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, base, USERNAME)
	await save(zing, otherBase, OTHER_USERNAME)
	const page = await open(zing, `${base}/login`)

	await post(page, { zingCredentialField: { kind: 'username', x: 10, y: 10, w: 200, h: 24, origin: otherBase, frameUrl: `${otherBase}/login` } })

	await expect.poll(async () => (await pickerState(zing))?.entries.map((e) => e.username)).toEqual([USERNAME])
	const ui = await picker(zing)
	await expect(ui.getByText(OTHER_USERNAME)).toHaveCount(0)
	await zing.close()
})

test('a forged field rect outside the page puts the picker inside the page, never over the chrome', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, base, USERNAME)
	const page = await open(zing, `${base}/login`)

	await post(page, { zingCredentialField: { kind: 'username', x: -200, y: -300, w: 300, h: 320 } })
	await expect.poll(async () => (await pickerLayer(zing))?.visible).toBe(true)
	await expect.poll(async () => (await pickerLayer(zing))!.height).toBeGreaterThan(20)
	const layer = (await pickerLayer(zing))!
	const pageLayer = (await layers(zing.chrome)).find((l) => l.url === `${base}/login`)!
	expect(layer.x).toBeGreaterThanOrEqual(pageLayer.x)
	expect(layer.y).toBeGreaterThanOrEqual(pageLayer.y)
	expect(layer.x + layer.width).toBeLessThanOrEqual(pageLayer.x + pageLayer.width + 0.5)
	expect(layer.y + layer.height).toBeLessThanOrEqual(pageLayer.y + pageLayer.height + 0.5)
	await zing.close()
})

test('malformed, oversized and too-deep page messages are dropped and counted, and the page still works', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, base, USERNAME)
	const page = await open(zing, `${base}/login`)
	const before = await droppedMessages(zing)

	await post(page, { zingCredentialField: { kind: 'key', key: 1 } })
	await post(page, { zingCredentialField: { kind: 'username', x: 'a', y: 0, w: 1, h: 1 } })
	await post(page, { zingCredentialField: { kind: 'username', x: 0, y: 0, w: 100, h: 20, pad: 'x'.repeat(5000) } })
	await page.evaluate(() => {
		let deep: unknown = 1
		for (let i = 0; i < 100; i++) deep = { deep }
		;(globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage(deep)
	})

	await expect.poll(async () => (await droppedMessages(zing)) - before).toBe(4)
	expect(await pickerState(zing)).toBeNull()
	await page.click('#user')
	await expect.poll(async () => (await pickerState(zing))?.entries.map((e) => e.username)).toEqual([USERNAME])
	// close() fails on any error recorded meanwhile: nothing threw out of the handler.
	await zing.close()
})

test('turning the vault off while a page is open hides the picker and stops offering; turning it on offers again', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, base, USERNAME)
	const page = await open(zing, `${base}/login`)
	await page.click('#user')
	await expect.poll(async () => (await pickerState(zing))?.entries.length).toBe(1)

	await zing.chrome.evaluate(() => window.zing.setSetting('passwordAutofill', false))
	await expect.poll(() => pickerState(zing)).toBeNull()
	await expect.poll(async () => (await pickerLayer(zing))?.visible).toBe(false)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getVaultState())).enabled).toBe(false)
	// Neither the page's script nor a forged message brings it back.
	await page.click('#pass')
	await post(page, { zingCredentialField: { kind: 'username', x: 10, y: 10, w: 200, h: 24 } })
	await settle(page)
	expect(await pickerState(zing)).toBeNull()

	await zing.chrome.evaluate(() => window.zing.setSetting('passwordAutofill', true))
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getVaultState())).count).toBe(1)
	// The channel comes back with the next document.
	await page.reload()
	await page.waitForLoadState('load')
	await page.click('#user')
	await expect.poll(async () => (await pickerState(zing))?.entries.map((e) => e.username)).toEqual([USERNAME])
	await zing.close()
})

test('with the vault off, no picker appears, even with a login saved for the page', async () => {
	const first = await launchZing({ userDataDir: vaultProfile(true) })
	await save(first, base, USERNAME)
	await first.close()
	const settings = join(first.userDataDir, 'settings.json')
	writeFileSync(settings, JSON.stringify({ ...JSON.parse(readFileSync(settings, 'utf8')), passwordAutofill: false }))

	const zing = await launchZing({ userDataDir: first.userDataDir })
	const page = await open(zing, `${base}/login`)
	await page.click('#user')
	await post(page, { zingCredentialField: { kind: 'username', x: 10, y: 10, w: 200, h: 24 } })
	await settle(page)

	expect(await pickerState(zing)).toBeNull()
	expect(await pickerLayer(zing)).toBeUndefined()
	await zing.close()
})

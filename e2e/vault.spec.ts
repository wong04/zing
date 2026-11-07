// The password vault's autofill (Task 4): the picker is Zing's own view, drawn under the field, so the page never
// sees a saved username; it follows the frame's real origin, never what the page says; a password reaches the page
// only after a choice and Windows Hello (faked in e2e only, through the vault-hello test hook).
import { mkdirSync, writeFileSync } from 'node:fs'
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

/** A profile with the vault on: passwordAutofill is read at launch. */
function vaultProfile(on: boolean): string {
	const dir = scratchDir('zing-host-data-')
	mkdirSync(dir, { recursive: true })
	writeFileSync(join(dir, 'settings.json'), JSON.stringify({ passwordAutofill: on }))
	return dir
}

async function hello(zing: Launched, result: 'pass' | 'cancel' | 'unavailable'): Promise<void> {
	await zing.chrome.evaluate((r) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'vault-hello', result: r }), result)
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

const picker = (zing: Launched): Promise<Page> => pageWhere(zing.browser, (p) => p.url().endsWith('#picker'))

// The bridge leaves out a null value, so a closed picker arrives as undefined.
const pickerState = async (zing: Launched) => (await zing.chrome.evaluate(() => window.zing.getAccountPicker())) ?? null

async function pickerLayer(zing: Launched) {
	return (await layers(zing.chrome)).find((l) => l.url.endsWith('#picker'))
}

/** Long enough for the detection script to report and the host to answer. */
const settle = (page: Page): Promise<void> => page.waitForTimeout(1200)

test('focusing the username box shows the saved username under it, and choosing it fills both boxes', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, `${base}/login`, USERNAME)
	const page = await open(zing, `${base}/login`)

	await page.click('#user')
	const ui = await picker(zing)
	await expect(ui.getByRole('option', { name: USERNAME })).toBeVisible()
	// Only the username: the picker never shows a password.
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

	await ui.getByRole('option', { name: USERNAME }).click()
	await expect(page.locator('#user')).toHaveValue(USERNAME)
	await expect(page.locator('#pass')).toHaveValue(PASSWORD)
	await expect.poll(() => pickerState(zing)).toBeNull()
	await expect.poll(async () => (await pickerLayer(zing))?.visible).toBe(false)
	await zing.close()
})

test('the picker is keyboard-navigable: arrow keys, Enter and Esc', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, `${base}/login`, USERNAME)
	await save(zing, `${base}/login`, 'second@example.test', 'second password')
	const page = await open(zing, `${base}/login`)

	await page.click('#pass')
	const ui = await picker(zing)
	await expect(ui.getByRole('option')).toHaveCount(2)
	// Esc in the page closes it, and the down arrow brings it back with the keyboard in it.
	await page.keyboard.press('Escape')
	await expect.poll(() => pickerState(zing)).toBeNull()
	await page.keyboard.press('ArrowDown')
	await expect.poll(async () => (await pickerState(zing))?.entries.length).toBe(2)
	const listbox = ui.getByRole('listbox')
	await expect(listbox).toBeFocused()
	const first = await ui.getByRole('option', { selected: true }).textContent()
	await ui.keyboard.press('ArrowDown')
	const second = await ui.getByRole('option', { selected: true }).textContent()
	expect(second).not.toBe(first)
	await ui.keyboard.press('ArrowUp')
	await expect(ui.getByRole('option', { selected: true })).toHaveText(first!)
	await ui.keyboard.press('ArrowDown')
	await ui.keyboard.press('Enter')
	const chosen = second!.trim()
	await expect(page.locator('#user')).toHaveValue(chosen)
	await expect(page.locator('#pass')).toHaveValue(chosen === USERNAME ? PASSWORD : 'second password')
	await expect.poll(() => pickerState(zing)).toBeNull()

	// Esc in the picker closes it without filling.
	await page.locator('#user').fill('')
	await page.keyboard.press('ArrowDown')
	await expect(listbox).toBeFocused()
	await ui.keyboard.press('Escape')
	await expect.poll(() => pickerState(zing)).toBeNull()
	await expect(page.locator('#user')).toHaveValue('')
	await zing.close()
})

test('with Windows Hello cancelled, choosing fills nothing, shows no error and closes the picker', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, `${base}/login`, USERNAME)
	const page = await open(zing, `${base}/login`)
	await hello(zing, 'cancel')

	await page.click('#user')
	const ui = await picker(zing)
	await ui.getByRole('option', { name: USERNAME }).click()

	await expect.poll(() => pickerState(zing)).toBeNull()
	await expect.poll(async () => (await pickerLayer(zing))?.visible).toBe(false)
	await settle(page)
	await expect(page.locator('#user')).toHaveValue('')
	await expect(page.locator('#pass')).toHaveValue('')
	// No notice or error dialog: no notice layer is on screen, and close() fails on any recorded error.
	expect((await layers(zing.chrome)).some((l) => l.url.endsWith('#overlay') && l.visible)).toBe(false)
	await zing.close()
})

test('an iframe from another local port gets no picker, while the top page still does', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(true) })
	await save(zing, base, USERNAME)
	await save(zing, otherBase, OTHER_USERNAME)
	const page = await open(zing, `${base}/framed`)
	const frame = page.frameLocator('#frame')

	await frame.locator('#user').click()
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

	await page.evaluate(
		(claimed) =>
			(globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({
				zingCredentialField: { kind: 'username', x: 10, y: 10, w: 200, h: 24, origin: claimed, frameUrl: `${claimed}/login` },
			}),
		otherBase,
	)

	await expect.poll(async () => (await pickerState(zing))?.entries.map((e) => e.username)).toEqual([USERNAME])
	const ui = await picker(zing)
	await expect(ui.getByText(OTHER_USERNAME)).toHaveCount(0)
	await zing.close()
})

test('with the vault off, no picker appears', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(false) })
	const page = await open(zing, `${base}/login`)

	await page.click('#user')
	await settle(page)

	expect(await pickerState(zing)).toBeNull()
	expect(await pickerLayer(zing)).toBeUndefined()
	await zing.close()
})

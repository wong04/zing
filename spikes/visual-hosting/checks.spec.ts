// W-6 visual hosting checks, driven over CDP like spikes/webview2. NOT YET RUN (written on Linux).
// Build: dotnet build -c Release spikes/visual-hosting
// Run:   npx playwright test -c spikes/visual-hosting/playwright.config.ts
//
// "host click" / "host move" below are test messages: the spike posts WM_MOUSEMOVE / WM_LBUTTONDOWN /
// WM_LBUTTONUP to its own window, so they go through the real WndProc -> hit-test -> SendMouseInput path.
// Only the OS's choice of window is skipped. CDP input (locator.click, keyboard.type) goes straight to one
// view's renderer and proves nothing about routing; it is used for the DevTools check, and for keys after
// focus has been moved by a host click.
import { spawn, type ChildProcess } from 'node:child_process'
import { join } from 'node:path'
import { chromium, expect, test, type Browser, type Page } from '@playwright/test'

const EXE = join(import.meta.dirname, 'bin', 'Release', 'net9.0-windows10.0.22621.0', 'win-x64', 'VisualHostingSpike.exe')

interface Box {
	x: number
	y: number
	width: number
	height: number
}

interface State {
	scale: number
	focused: string | null
	hovered: string | null
	cursors: Record<string, string>
	views: Record<'chrome' | 'page' | 'overlay', Box>
	overlayHitRects: number
	events: string[]
}

interface Pixel {
	r: number
	g: number
	b: number
}

interface Spike {
	process: ChildProcess
	browser: Browser
	chrome: Page
	page: Page
	overlay: Page
}

let nextPort = 9500

function pages(browser: Browser): Page[] {
	return browser.contexts().flatMap((c) => c.pages())
}

async function view(browser: Browser, file: string): Promise<Page> {
	const match = (p: Page) => p.url().endsWith(`/${file}`)
	await expect.poll(() => pages(browser).some(match), { timeout: 15_000 }).toBe(true)
	return pages(browser).find(match)!
}

async function launch(): Promise<Spike> {
	const port = String(nextPort++)
	const process_ = spawn(EXE, [], { env: { ...process.env, ZING_E2E: '1', ZING_SPIKE_CDP: port }, stdio: 'ignore' })
	await expect
		.poll(async () => (await fetch(`http://127.0.0.1:${port}/json/version`).catch(() => undefined))?.ok ?? false, { timeout: 30_000 })
		.toBe(true)
	const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`)
	const spike = { process: process_, browser, chrome: await view(browser, 'chrome.html'), page: await view(browser, 'page.html'), overlay: await view(browser, 'overlay.html') }
	// Ready when the overlay has told the host where its content is.
	await expect.poll(async () => (await state(spike)).overlayHitRects, { timeout: 15_000 }).toBeGreaterThan(0)
	return spike
}

async function close(spike: Spike): Promise<void> {
	await spike.browser.close().catch(() => undefined)
	spike.process.kill()
}

/** Sends a test message through the chrome view and waits for the host's reply. */
async function ask<T>(spike: Spike, message: string, extra: Record<string, number> = {}): Promise<T> {
	const id = await spike.chrome.evaluate(([m, e]) => (window as unknown as { spike: (t: string, x: object) => number }).spike(m, e), [message, extra] as const)
	const reply = () => spike.chrome.evaluate((i) => (window as unknown as { __replies: { id: number; reply: unknown }[] }).__replies.find((r) => r.id === i), id)
	await expect.poll(async () => (await reply()) !== undefined, { timeout: 10_000 }).toBe(true)
	return (await reply())!.reply as T
}

const state = (spike: Spike) => ask<State>(spike, 'state')

/** The centre of an element in one view, in the window's client DIPs. */
async function centre(spike: Spike, name: 'chrome' | 'page' | 'overlay', selector: string): Promise<{ x: number; y: number }> {
	const box = (await spike[name].locator(selector).boundingBox())!
	const origin = (await state(spike)).views[name]
	return { x: origin.x + box.x + box.width / 2, y: origin.y + box.y + box.height / 2 }
}

function near(actual: Pixel, expected: Pixel): boolean {
	return Math.abs(actual.r - expected.r) <= 8 && Math.abs(actual.g - expected.g) <= 8 && Math.abs(actual.b - expected.b) <= 8
}

const PAGE_RED = { r: 232, g: 85, b: 62 }
const OVERLAY_BLUE = { r: 51, g: 85, b: 204 }
const CHROME_BROWN = { r: 43, g: 37, b: 32 }

test('layout: the page sits under the chrome and the overlay covers the page', async () => {
	const spike = await launch()
	const { views } = await state(spike)
	expect(views.chrome).toMatchObject({ x: 0, y: 0 })
	expect(Math.round(views.page.y)).toBe(Math.round(views.chrome.height))
	expect(views.overlay).toEqual(views.page)
	await close(spike)
})

test('the overlay draws over the page, and the page shows through where it has no content (window pixels)', async () => {
	const spike = await launch()
	const { views } = await state(spike)
	// Overlay header over the page: the window shows the overlay's blue, not the page beneath.
	const head = await centre(spike, 'overlay', '.head')
	expect(near(await ask<Pixel>(spike, 'pixel', head), OVERLAY_BLUE)).toBe(true)
	// The bottom-right of the page has no overlay content: the transparent overlay shows the page's red.
	const bare = { x: views.page.x + views.page.width - 30, y: views.page.y + views.page.height - 30 }
	expect(near(await ask<Pixel>(spike, 'pixel', bare), PAGE_RED)).toBe(true)
	// The chrome strip is its own visual above both.
	expect(near(await ask<Pixel>(spike, 'pixel', { x: views.chrome.width - 8, y: views.chrome.height / 2 }), CHROME_BROWN)).toBe(true)
	await close(spike)
})

test('a DevTools-dispatched click on an overlay row reaches the overlay DOM', async () => {
	// Today's gap: CDP input never reached #overlay-stack in its owned popup.
	const spike = await launch()
	await spike.overlay.locator('[data-row="2"]').click()
	await expect.poll(() => spike.overlay.title()).toBe('chose:2')
	// And the host relayed the choice to the page.
	await expect(spike.page.locator('#chosen')).toHaveText('chosen 2')
	await close(spike)
})

test('host-routed clicks land on the topmost view that wants the point', async () => {
	const spike = await launch()
	// Over the overlay's panel: the overlay row gets it, and the page element under the panel does not.
	await ask(spike, 'click', await centre(spike, 'overlay', '[data-row="3"]'))
	await expect.poll(() => spike.overlay.title()).toBe('chose:3')
	expect(await spike.page.evaluate(() => (window as unknown as { __clicks: string[] }).__clicks)).not.toContain('under')
	// Over the overlay's transparent part: the page beneath gets it.
	await ask(spike, 'click', await centre(spike, 'page', '#link'))
	await expect.poll(() => spike.page.evaluate(() => (window as unknown as { __clicks: string[] }).__clicks)).toContain('link')
	const { events } = await state(spike)
	expect(events).toContain('mouse 0x201 -> overlay')
	expect(events).toContain('mouse 0x201 -> page')
	await close(spike)
})

test('keyboard focus moves to the clicked view, and each view then takes keys', async () => {
	const spike = await launch()
	await ask(spike, 'click', await centre(spike, 'page', '#field'))
	await expect.poll(async () => (await state(spike)).focused).toBe('page')
	await expect.poll(() => spike.page.evaluate(() => document.activeElement?.id)).toBe('field')
	await spike.page.keyboard.type('page keys')
	await expect(spike.page.locator('#field')).toHaveValue('page keys')

	await ask(spike, 'click', await centre(spike, 'overlay', '#filter'))
	await expect.poll(async () => (await state(spike)).focused).toBe('overlay')
	await expect.poll(() => spike.overlay.evaluate(() => document.activeElement?.id)).toBe('filter')
	await spike.overlay.keyboard.type('overlay keys')
	await expect(spike.overlay.locator('#filter')).toHaveValue('overlay keys')
	await expect(spike.page.locator('#field')).toHaveValue('page keys')

	// And back: a click on the chrome moves focus there.
	await ask(spike, 'click', await centre(spike, 'chrome', '#address'))
	await expect.poll(async () => (await state(spike)).focused).toBe('chrome')
	await close(spike)
})

test('the cursor follows the view under the pointer', async () => {
	const spike = await launch()
	await ask(spike, 'move', await centre(spike, 'page', '#link'))
	await expect.poll(async () => (await state(spike)).cursors.page).toBe('Hand')
	expect((await state(spike)).hovered).toBe('page')
	await ask(spike, 'move', await centre(spike, 'page', '#text'))
	await expect.poll(async () => (await state(spike)).cursors.page).toBe('IBeam')
	await ask(spike, 'move', await centre(spike, 'overlay', '[data-row="1"]'))
	await expect.poll(async () => (await state(spike)).cursors.overlay).toBe('Hand')
	expect((await state(spike)).hovered).toBe('overlay')
	await close(spike)
})

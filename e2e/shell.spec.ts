import { execFileSync } from 'node:child_process'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchBare, launchZing, test, layers, pages, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

/**
 * Every file under a folder, so a test can prove a secret is nowhere on disk. The engine may
 * still be deleting its temp files as Zing exits; a file that is gone holds nothing.
 */
function filesUnder(dir: string): string[] {
	return readdirSync(dir).flatMap((name) => {
		const path = join(dir, name)
		try {
			return statSync(path).isDirectory() ? filesUnder(path) : [path]
		} catch (error) {
			if ((error as NodeJS.ErrnoException).code === 'ENOENT') return []
			throw error
		}
	})
}

/** A file's bytes, or none when the engine deleted it after it was listed. */
function contentsOf(file: string): Buffer {
	try {
		return readFileSync(file)
	} catch (error) {
		if ((error as NodeJS.ErrnoException).code === 'ENOENT') return Buffer.alloc(0)
		throw error
	}
}

const isSurface =(url: string) => url.endsWith('#surface')

test('Settings opens over the window, a changed toggle survives a relaunch, and Close puts it away', async () => {
	const first = await launchZing()
	await first.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(first.browser, (p) => isSurface(p.url()))
	await expect(sheet.getByRole('dialog', { name: 'Settings' })).toBeVisible()
	await sheet.getByRole('button', { name: 'Browsing' }).click()
	await sheet.getByRole('switch', { name: 'Reopen tabs from last time' }).click()
	await expect(sheet.getByRole('switch', { name: 'Reopen tabs from last time' })).toHaveAttribute('aria-checked', 'true')
	await sheet.getByRole('button', { name: 'Close settings (Esc)' }).click()
	await expect(sheet.getByRole('dialog', { name: 'Settings' })).toHaveCount(0)
	await expect.poll(async () => (await first.chrome.evaluate(() => window.zing.getShell())).settingsOpen).toBe(false)
	await first.close()

	const second = await launchZing({ userDataDir: first.userDataDir })
	expect((await second.chrome.evaluate(() => window.zing.getSettings())).restoreTabs).toBe(true)
	await second.close()
})

test('Skip for now takes the gate away and leaves the browser showing', async () => {
	const zing = await launchZing({ env: { ZING_SKIP_ACCOUNT_GATE: '' } })
	const gate = await pageWhere(zing.browser, (p) => isSurface(p.url()))
	await gate.getByRole('button', { name: 'Skip for now' }).click()
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getShell())).gated).toBe(false)
	// One surface view only, and hidden: a second one left on top covered the whole window in gray.
	expect(pages(zing.browser).filter((p) => isSurface(p.url()))).toHaveLength(1)
	await expect.poll(async () => (await layers(zing.chrome)).filter((l) => isSurface(l.url) && l.visible)).toEqual([])
	await zing.close()
})

test('the account gate covers the window, stores no plaintext secret on disk, and remembers the user', async () => {
	const password = 'Zing-e2e-secret-7Q-pass'
	const first = await launchZing({ env: { ZING_SKIP_ACCOUNT_GATE: '' } })
	const gate = await pageWhere(first.browser, (p) => isSurface(p.url()))
	await expect(gate.getByRole('heading', { name: 'Create your account' })).toBeVisible()
	// Behind the gate nothing acts on the browser.
	expect((await first.chrome.evaluate(() => window.zing.runCommand('tab.new'))).status).toBe('not-applicable')
	await gate.getByLabel('Username').fill('jdoe')
	await gate.getByLabel('Display name').fill('Jane Doe')
	await gate.getByLabel('Password', { exact: true }).fill(password)
	await gate.getByLabel('Confirm').fill(password)
	await gate.getByRole('button', { name: 'Create Account' }).click()
	await expect(gate.locator('.auth')).toHaveCount(0)
	await first.close()

	const secret = Buffer.from(password)
	const files = filesUnder(first.userDataDir)
	expect(files.some((f) => f.endsWith('account.verifier'))).toBe(true)
	for (const file of files) expect(contentsOf(file).includes(secret), file).toBe(false)

	const second = await launchZing({ userDataDir: first.userDataDir, env: { ZING_SKIP_ACCOUNT_GATE: '' } })
	expect(await second.chrome.evaluate(() => window.zing.getAccount())).toMatchObject({ hasAccount: true, signedIn: true })
	await second.close()
})

test('the pinned sidebar sits flush with the window’s top, left and bottom edges, as the app sidebar or as the tab rail', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate(() => window.zing.setSetting('sidebarPinned', true))
	const height = await zing.chrome.evaluate(() => (globalThis as unknown as { innerHeight: number }).innerHeight)
	const flush = async (selector: string): Promise<void> => {
		const sidebar = zing.chrome.locator(selector)
		await expect(sidebar).toBeVisible()
		const box = (await sidebar.boundingBox())!
		expect([box.x, box.y, box.y + box.height]).toEqual([0, 0, height])
	}
	// Tabs on the left (the default): the rail is the sidebar.
	await flush('.rail-slot > .rail')
	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
	await flush('.sidebar-slot > .sidebar')
	await zing.close()
})

test('the empty favourites bar’s Ctrl+D hint closes, and stays closed after a relaunch', async () => {
	const zing = await launchZing()
	// The bar shows with tabs on top.
	await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
	const hint = zing.chrome.locator('.fav-hint')
	await expect(hint).toBeVisible()
	await hint.getByRole('button', { name: 'Hide this hint' }).click()
	await expect(hint).toHaveCount(0)
	await zing.close()

	const again = await launchZing({ userDataDir: zing.userDataDir })
	await expect(again.chrome.locator('.favourites-bar')).toBeVisible()
	await expect(again.chrome.locator('.fav-hint')).toHaveCount(0)
	await again.close()
})

/** Whether Windows has apps in dark mode: what a view reads when nothing overrides it. */
function windowsIsDark(): boolean {
	const key = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize'
	return /AppsUseLightTheme\s+REG_DWORD\s+0x0\b/.test(execFileSync('reg', ['query', key, '/v', 'AppsUseLightTheme'], { encoding: 'utf8' }))
}

const readsDark = (view: Page) => view.evaluate(() => matchMedia('(prefers-color-scheme: dark)').matches)

test('Appearance forces the chrome, overlays and pages to its scheme whatever Windows is set to, and System follows Windows', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => isSurface(p.url()))
	const home = await pageWhere(zing.browser, (p) => p.url().startsWith('zing://home'))
	const views = [zing.chrome, sheet, home]
	// Playwright emulates light on every page it attaches to, which would hide what the host does.
	for (const view of views) await view.emulateMedia({ colorScheme: null })

	for (const [theme, dark] of [['light', false], ['dark', true], ['light', false], ['system', windowsIsDark()]] as const) {
		await zing.chrome.evaluate((value) => window.zing.setSetting('theme', value), theme)
		for (const view of views) await expect.poll(() => readsDark(view), `${view.url()} on ${theme}`).toBe(dark)
	}

	// A chosen scheme survives a reload, and the tokens really change with it.
	await zing.chrome.evaluate(() => window.zing.setSetting('theme', 'light'))
	await home.reload()
	await expect.poll(() => readsDark(home)).toBe(false)
	await zing.chrome.evaluate(() => window.zing.setSetting('theme', 'dark'))
	await expect.poll(() => zing.chrome.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg').trim())).toBe('#1A1714')

	// Views take the scheme from the start, on a relaunch and when created later, read with no Playwright to
	// disturb them. The opposite of Windows' proves it.
	const dark = !windowsIsDark()
	await zing.chrome.evaluate((value) => window.zing.setSetting('theme', value), dark ? 'dark' : 'light')
	await zing.close()

	const bare = await launchBare(zing.userDataDir)
	const everyViewReads = async (scheme: boolean) => (await bare.evaluate(() => true, "matchMedia('(prefers-color-scheme: dark)').matches")).every((matches) => matches === scheme)
	await expect.poll(() => bare.urls()).toContainEqual(expect.stringContaining('zing://home'))
	expect(await everyViewReads(dark)).toBe(true)
	await bare.evaluate((url) => url.endsWith('#chrome'), "zing.runCommand('tab.new')")
	await bare.evaluate((url) => url.endsWith('#chrome'), "zing.runCommand('app.settings')")
	await expect.poll(async () => (await bare.urls()).filter((url) => url.startsWith('zing://home') || isSurface(url)).length).toBe(3)
	expect(await everyViewReads(dark)).toBe(true)
	await bare.close()
})

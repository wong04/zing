import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { expect, test, type ElectronApplication, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing } from './zing'

// The e2e project has no DOM types, so the page's bridge is reached through globalThis.
type PageGlobal = { zing: ZingApi }

function isSurface(page: Page): boolean {
	return URL.canParse(page.url()) && page.url().endsWith('#surface')
}

async function surfaceOf(app: ElectronApplication): Promise<Page> {
	const page = app.windows().find(isSurface) ?? (await app.waitForEvent('window', { predicate: isSurface }))
	await page.waitForLoadState('domcontentloaded')
	return page
}

/** Every file under a folder, so a test can prove a secret is nowhere on disk. */
function filesUnder(dir: string): string[] {
	return readdirSync(dir).flatMap((name) => {
		const path = join(dir, name)
		return statSync(path).isDirectory() ? filesUnder(path) : [path]
	})
}

function pageViewBounds(app: ElectronApplication) {
	return app.evaluate(({ BrowserWindow }) => {
		const [window] = BrowserWindow.getAllWindows()
		return window?.contentView.children.map((v) => v.getBounds()) ?? []
	})
}

test('Settings opens with Ctrl+, and a changed toggle survives a relaunch', async () => {
	const first = await launchZing()
	await first.chrome.evaluate(() => (globalThis as unknown as PageGlobal).zing.runCommand('app.settings'))
	const sheet = await surfaceOf(first.app)
	await expect(sheet.getByRole('dialog', { name: 'Settings' })).toBeVisible()

	await sheet.getByRole('button', { name: 'Browsing' }).click()
	await sheet.getByRole('switch', { name: 'Reopen tabs from last time' }).click()
	await expect(sheet.getByRole('switch', { name: 'Reopen tabs from last time' })).toHaveAttribute('aria-checked', 'true')

	await sheet.getByRole('button', { name: 'Privacy' }).click()
	await expect(sheet.locator('.privacy-banner')).toHaveAttribute('data-traceless', 'false')
	await sheet.getByRole('button', { name: 'Close settings (Esc)' }).click()
	await expect(sheet.getByRole('dialog', { name: 'Settings' })).toHaveCount(0)
	await first.app.close()

	const second = await launchZing({ userDataDir: first.userDataDir })
	const settings = await second.chrome.evaluate(() => (globalThis as unknown as PageGlobal).zing.getSettings())
	expect(settings.restoreTabs).toBe(true)
	await second.chrome.evaluate(() => (globalThis as unknown as PageGlobal).zing.runCommand('app.settings'))
	const again = await surfaceOf(second.app)
	await again.getByRole('button', { name: 'Privacy' }).click()
	await expect(again.locator('.privacy-banner')).toHaveAttribute('data-traceless', 'false')
	await again.getByRole('button', { name: 'Browsing' }).click()
	await again.getByRole('switch', { name: 'Reopen tabs from last time' }).click()
	await again.getByRole('button', { name: 'Privacy' }).click()
	await expect(again.locator('.privacy-banner')).toHaveAttribute('data-traceless', 'true')
	await second.app.close()
})

test('the sidebar pins and unpins, and the page view follows whole pixels', async () => {
	const { app, chrome } = await launchZing()
	await expect(chrome.locator('.sidebar-slot')).toBeVisible()
	expect(await pageViewBounds(app)).toContainEqual(expect.objectContaining({ x: 204 }))

	await chrome.evaluate(() => (globalThis as unknown as PageGlobal).zing.runCommand('view.sidebar'))
	await expect(chrome.locator('.sidebar-slot')).toHaveCount(0)
	const bounds = await pageViewBounds(app)
	expect(bounds).toContainEqual(expect.objectContaining({ x: 0 }))
	for (const b of bounds) for (const n of [b.x, b.y, b.width, b.height]) expect(Number.isInteger(n)).toBe(true)

	// Unpinning shows the sidebar for a moment, drawn over the page by the surface view.
	const surface = await surfaceOf(app)
	await expect(surface.locator('.sidebar.floating')).toBeVisible()
	await expect(surface.locator('.sidebar.floating')).toHaveCount(0, { timeout: 8000 })

	await chrome.evaluate(() => (globalThis as unknown as PageGlobal).zing.runCommand('view.sidebar'))
	await expect(chrome.locator('.sidebar-slot')).toBeVisible()
	expect(await pageViewBounds(app)).toContainEqual(expect.objectContaining({ x: 204 }))
	await app.close()
})

test('a recent tool tears off into a panel and the fourth panel is refused', async () => {
	const { app, chrome } = await launchZing({
		settings: { recentFeatures: ['tool:Hashes', 'tool:JWT Decoder', 'tool:Regex Tester'] },
	})
	const row = chrome.getByTestId('recent-row').first()
	await expect(row).toBeVisible()
	const box = (await row.boundingBox())!
	await chrome.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
	await chrome.mouse.down()
	await chrome.waitForTimeout(550)
	await chrome.mouse.move(box.x + 500, box.y + 150, { steps: 5 })
	await chrome.mouse.up()

	const surface = await surfaceOf(app)
	await expect(surface.getByTestId('float-panel')).toHaveCount(1)

	// openPanel takes screen coordinates. Test windows sit far off-screen, so a
	// fixed screen point lands outside the window; aim inside it instead.
	const content = await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]!.getContentBounds())
	const at = { x: content.x + 800, y: content.y + 400 }
	const open = (panel: string) => chrome.evaluate(([c, point]) => (globalThis as unknown as PageGlobal).zing.openPanel(c, point), [panel, at] as const)
	expect((await open('tool:JWT Decoder')).status).toBe('opened')
	expect((await open('tool:Regex Tester')).status).toBe('opened')
	expect(await open('tool:Base64')).toEqual({ status: 'full' })
	await expect(surface.locator('[data-testid=float-panel]:not([hidden])')).toHaveCount(3)

	// Minimizing frees a slot and lists the panel in the sidebar.
	await surface.getByRole('button', { name: /Minimize Hashes/ }).click()
	await expect(chrome.getByTestId('minimized-row')).toHaveCount(1)
	expect((await open('tool:Base64')).status).toBe('opened')
	await app.close()
})

test('the account gate stores no plaintext secret on disk', async () => {
	const password = 'Zing-e2e-secret-7Q-pass'
	const first = await launchZing({ env: { ZING_SKIP_ACCOUNT_GATE: '' } })
	const gate = await surfaceOf(first.app)
	await expect(gate.getByRole('heading', { name: 'Create your account' })).toBeVisible()
	await gate.getByLabel('Username').fill('jdoe')
	await gate.getByLabel('Display name').fill('Jane Doe')
	await gate.getByLabel('Password', { exact: true }).fill(password)
	await gate.getByLabel('Confirm').fill(password)
	await gate.getByRole('button', { name: 'Create Account' }).click()
	await expect(gate.locator('.auth')).toHaveCount(0)
	await first.app.close()

	const secret = Buffer.from(password)
	const files = filesUnder(first.userDataDir)
	expect(files.some((f) => f.endsWith('account.verifier'))).toBe(true)
	for (const file of files) expect(readFileSync(file).includes(secret), file).toBe(false)

	// A returning user is signed in again without the gate.
	const second = await launchZing({ userDataDir: first.userDataDir, env: { ZING_SKIP_ACCOUNT_GATE: '' } })
	const account = await second.chrome.evaluate(() => (globalThis as unknown as PageGlobal).zing.getAccount())
	expect(account).toMatchObject({ hasAccount: true, signedIn: true })
	await second.app.close()
})

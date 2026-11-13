// Settings, Import: browser profiles found under a fake LocalAppData (ZING_LOCALAPPDATA, built from the synthetic
// fixtures in src/shared/import/fixtures), imported into the library, and a bookmarks file picked through the pick-files
// test message. With ZING_SCREENSHOTS=1 it also takes the section's pictures, light and dark, into docs/screenshots.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import type { BookmarkNode } from '../src/shared/library/library'
import { viewAccent } from './theme-helpers'
import { launchZing, pages, pageWhere, scratchDir, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const FIXTURES = join(import.meta.dirname, '..', 'src', 'shared', 'import', 'fixtures')
const SHOTS = join(import.meta.dirname, '..', 'docs', 'screenshots')
const fixture = (name: string): string => readFileSync(join(FIXTURES, name), 'utf8')

function write(path: string, text: string): void {
	mkdirSync(join(path, '..'), { recursive: true })
	writeFileSync(path, text)
}

/** A fake LocalAppData holding Chrome profiles (directory, display name, Bookmarks text) and optionally Arc. */
function localAppData(profiles: readonly (readonly [string, string, string])[], arc = false): string {
	const root = scratchDir('zing-localappdata-')
	if (profiles.length > 0) {
		const userData = join(root, 'Google', 'Chrome', 'User Data')
		const info_cache = Object.fromEntries(profiles.map(([dir, name]) => [dir, { name }]))
		write(join(userData, 'Local State'), JSON.stringify({ profile: { info_cache } }))
		for (const [dir, , bookmarks] of profiles) write(join(userData, dir, 'Bookmarks'), bookmarks)
	}
	if (arc) write(join(root, 'Packages', 'TheBrowserCompany.Arc_ttt1ap7aakyb4', 'LocalCache', 'Local', 'Arc', 'StorableSidebar.json'), fixture('arc-sidebar.json'))
	return root
}

const launch = (root: string, env: Readonly<Record<string, string>> = {}): Promise<Launched> => launchZing({ env: { ZING_LOCALAPPDATA: root, ...env } })

async function openImport(zing: Launched): Promise<Page> {
	await zing.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await sheet.getByRole('button', { name: 'Import', exact: true }).click()
	await sheet.getByRole('group', { name: 'Import', exact: true }).waitFor()
	return sheet
}

const section = (sheet: Page) => sheet.getByRole('group', { name: 'Import', exact: true })
const row = (sheet: Page, name: string) => section(sheet).getByRole('group', { name, exact: true })
const importFrom = (sheet: Page, name: string) => row(sheet, name).getByRole('button', { name: 'Import bookmarks' }).click()

const library = (zing: Launched) => zing.chrome.evaluate(() => window.zing.getLibrary()).then((state) => state.library)
const titles = (nodes: readonly BookmarkNode[]): string[] => nodes.map((n) => n.title)

function links(nodes: readonly BookmarkNode[]): string[] {
	return nodes.flatMap((n) => ('children' in n ? links(n.children) : [n.url]))
}

const topFolder = (bookmarks: readonly BookmarkNode[], title: string) => bookmarks.find((n) => n.title === title && 'children' in n)

test('Settings Import lists Chrome profiles and Arc found on this PC, and importing one adds its bar and an Imported folder', async () => {
	const root = localAppData([['Default', 'Personal', fixture('chromium-bookmarks.json')]], true)
	const zing = await launch(root)
	const sheet = await openImport(zing)
	await expect(row(sheet, 'Chrome (Personal)')).toBeVisible()
	await expect(row(sheet, 'Arc')).toBeVisible()
	await sheet.getByRole('button', { name: 'Privacy', exact: true }).click()
	await expect(sheet.locator('.privacy-banner')).toHaveAttribute('data-traceless', 'true')
	await sheet.getByRole('button', { name: 'Import', exact: true }).click()

	await importFrom(sheet, 'Chrome (Personal)')
	await expect(row(sheet, 'Chrome (Personal)').getByRole('status')).toContainText('Imported from Chrome (Personal)')

	const { bookmarks } = await library(zing)
	const bar = topFolder(bookmarks, 'Favourites')
	expect(bar && 'children' in bar ? titles(bar.children) : []).toEqual(expect.arrayContaining(['GitHub', 'Dev']))
	const imported = topFolder(bookmarks, 'Imported from Chrome (Personal)')
	expect(imported && 'children' in imported ? links(imported.children) : []).toContain('http://news.example/')

	await sheet.getByRole('button', { name: 'Privacy', exact: true }).click()
	await expect(sheet.locator('.privacy-banner')).toHaveAttribute('data-traceless', 'false')
	await expect(sheet.locator('.privacy-banner')).toContainText('Imported bookmarks are written to disk.')
	await zing.close()
})

test('importing the same profile twice adds nothing the second time', async () => {
	const zing = await launch(localAppData([['Default', 'Personal', fixture('chromium-bookmarks.json')]]))
	const sheet = await openImport(zing)
	await importFrom(sheet, 'Chrome (Personal)')
	await expect(row(sheet, 'Chrome (Personal)').getByRole('status')).toContainText('Added')
	const first = await library(zing)

	await importFrom(sheet, 'Chrome (Personal)')
	await expect(row(sheet, 'Chrome (Personal)').getByRole('status')).toContainText('Nothing new to import')
	expect((await library(zing)).bookmarks).toEqual(first.bookmarks)
	await zing.close()
})

const macRow = (sheet: Page, app: string) => section(sheet).getByRole('group', { name: `${app} on a Mac`, exact: true })

test('a bookmarks file from Safari imports into a folder named after the file', async () => {
	const dir = scratchDir('zing-bookmarks-file-')
	const file = join(dir, 'safari-export.html')
	writeFileSync(file, fixture('netscape-safari.html'))
	const zing = await launch(localAppData([]))
	const sheet = await openImport(zing)
	await zing.chrome.evaluate((f) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: [f] }), file)

	await macRow(sheet, 'Safari').getByRole('button', { name: 'Bookmarks file…' }).click()
	await expect(macRow(sheet, 'Safari').getByRole('status')).toContainText('safari-export.html')

	const { bookmarks } = await library(zing)
	const imported = bookmarks.find((n) => n.title.includes('safari-export.html') && 'children' in n)
	expect(imported).toBeDefined()
	const urls = imported && 'children' in imported ? links(imported.children) : []
	expect(urls).toContain('https://read.example/article?x=1&y=2')
	expect(urls.some((u) => u.startsWith('file:'))).toBe(false)
	await zing.close()
})

test('a broken profile shows its error and the others still import', async () => {
	const zing = await launch(
		localAppData([
			['Default', 'Personal', fixture('chromium-bookmarks.json')],
			['Profile 1', 'Work', fixture('invalid.json')],
		]),
	)
	const sheet = await openImport(zing)
	await expect(row(sheet, 'Chrome (Work)').getByRole('button', { name: 'Import bookmarks' })).toBeDisabled()
	await expect(row(sheet, 'Chrome (Work)')).not.toContainText(/\d+ bookmarks?/)
	await expect(row(sheet, 'Chrome (Work)')).toContainText('damaged or incomplete')

	await importFrom(sheet, 'Chrome (Personal)')
	await expect(row(sheet, 'Chrome (Personal)').getByRole('status')).toContainText('Added')
	expect(topFolder((await library(zing)).bookmarks, 'Imported from Chrome (Personal)')).toBeDefined()
	await zing.close()
})

test('From a Mac lists Dia, Safari and Arc with their export steps, and hides Passwords file while the vault is off', async () => {
	const zing = await launch(localAppData([]))
	const sheet = await openImport(zing)
	for (const app of ['Dia', 'Safari', 'Arc']) {
		await expect(macRow(sheet, app).getByRole('button', { name: 'Bookmarks file…' })).toBeVisible()
		await expect(macRow(sheet, app).getByRole('list', { name: `Exporting from ${app}` })).toContainText('Bookmarks:')
		await expect(macRow(sheet, app).getByRole('button', { name: 'Passwords file…' })).toHaveCount(0)
	}
	await expect(macRow(sheet, 'Dia')).toContainText('Open the Bookmarks menu and choose Export Bookmarks.')
	await expect(macRow(sheet, 'Arc')).toContainText('StorableSidebar.json')
	await zing.close()
})

test("Arc's sidebar file from a Mac imports", async () => {
	const file = join(scratchDir('zing-arc-file-'), 'StorableSidebar.json')
	writeFileSync(file, fixture('arc-sidebar.json'))
	const zing = await launch(localAppData([]))
	const sheet = await openImport(zing)
	await zing.chrome.evaluate((f) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: [f] }), file)

	await macRow(sheet, 'Arc').getByRole('button', { name: 'Bookmarks file…' }).click()
	await expect(macRow(sheet, 'Arc').getByRole('status')).toContainText('StorableSidebar.json')
	expect((await library(zing)).bookmarks.some((n) => n.title.includes('StorableSidebar.json') && 'children' in n)).toBe(true)
	await zing.close()
})

const PASSWORDS_CSV = 'name,url,username,password,note\nExample,https://example.test/login,ada,first password,\nOther,https://other.test,bob,second password,\n'

/** A profile with the vault on or off (passwordAutofill is read at launch) and a downloads folder, for the password flow. */
function passwordProfile(vaultOn: boolean): string {
	const dir = scratchDir('zing-host-data-')
	mkdirSync(join(dir, 'downloads'), { recursive: true })
	writeFileSync(join(dir, 'settings.json'), JSON.stringify({ passwordAutofill: vaultOn }))
	return dir
}

const launchWithPasswords = (vaultOn: boolean): Promise<Launched> =>
	launchZing({ userDataDir: passwordProfile(vaultOn), env: { ZING_LOCALAPPDATA: localAppData([['Default', 'Personal', fixture('chromium-bookmarks.json')]]) } })

const CHROME_LAUNCH = { kind: 'launch:chrome', url: 'chrome://password-manager/settings', args: ['--profile-directory=Default', 'chrome://password-manager/settings'] }

const passwordFlow = (sheet: Page) => row(sheet, 'Chrome (Personal)').getByRole('group', { name: 'Import Chrome passwords' })
const summaries = (zing: Launched) => zing.chrome.evaluate(() => window.zing.vaultSummaries())

test("Import passwords opens Chrome's export page, imports the CSV that lands in Downloads, and recycles it on request", async () => {
	const zing = await launchWithPasswords(true)
	const sheet = await openImport(zing)
	await row(sheet, 'Chrome (Personal)').getByRole('button', { name: 'Import passwords' }).click()
	await expect(passwordFlow(sheet)).toContainText('In Chrome, click Export passwords, confirm, and save. Zing will pick up the file.')
	await expect(passwordFlow(sheet)).toContainText('If Chrome shows a new tab instead, open ⋮ menu > Passwords and autofill > Google Password Manager > Settings.')
	expect(zing.dialogs()).toContainEqual(CHROME_LAUNCH)

	// The watch only takes a file written after it started, once its length has held for two polls.
	const file = join(zing.userDataDir, 'downloads', 'Chrome Passwords.csv')
	writeFileSync(file, PASSWORDS_CSV)
	await expect(passwordFlow(sheet).getByRole('status')).toContainText('Added 2, merged 0 into logins you already had, skipped 0', { timeout: 15_000 })
	await expect(passwordFlow(sheet)).toContainText('The exported file has every password in plain text.')
	expect((await summaries(zing)).map((l) => [l.origin, l.username]).sort()).toEqual([
		['https://example.test', 'ada'],
		['https://other.test', 'bob'],
	])
	expect(existsSync(file)).toBe(true)

	await passwordFlow(sheet).getByRole('button', { name: 'Move it to the Recycle Bin' }).click()
	await expect(passwordFlow(sheet).getByRole('button', { name: 'Done' })).toBeVisible()
	expect(existsSync(file)).toBe(false)
	expect(zing.dialogs()).toContainEqual({ kind: 'recycle', url: 'Chrome Passwords.csv' })
	await zing.close()
})

test('with the vault off, Import passwords asks first and turns it on', async () => {
	const zing = await launchWithPasswords(false)
	const sheet = await openImport(zing)
	expect((await zing.chrome.evaluate(() => window.zing.getVaultState())).enabled).toBe(false)
	await row(sheet, 'Chrome (Personal)').getByRole('button', { name: 'Import passwords' }).click()
	await expect(passwordFlow(sheet)).toContainText('Importing passwords turns on Zing’s password vault, which keeps them on this PC.')
	expect(zing.dialogs().some((d) => d.kind === 'launch:chrome')).toBe(false)
	expect((await zing.chrome.evaluate(() => window.zing.getVaultState())).enabled).toBe(false)

	await passwordFlow(sheet).getByRole('button', { name: 'Turn on and continue' }).click()
	await expect(passwordFlow(sheet)).toContainText('In Chrome, click Export passwords')
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getVaultState())).enabled).toBe(true)
	expect(zing.dialogs()).toContainEqual(CHROME_LAUNCH)
	await zing.close()
})

test('a passwords file chosen by hand imports and offers to recycle it', async () => {
	const zing = await launchWithPasswords(true)
	const sheet = await openImport(zing)
	const file = join(scratchDir('zing-passwords-file-'), 'my-export.csv')
	writeFileSync(file, PASSWORDS_CSV)
	await row(sheet, 'Chrome (Personal)').getByRole('button', { name: 'Import passwords' }).click()
	await expect(passwordFlow(sheet)).toContainText('Zing will pick up the file.')
	await zing.chrome.evaluate((f) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: [f] }), file)

	await passwordFlow(sheet).getByRole('button', { name: 'Choose the file myself…' }).click()
	await expect(passwordFlow(sheet).getByRole('status')).toContainText('Added 2, merged 0')
	await expect(passwordFlow(sheet)).toContainText('The exported file has every password in plain text.')
	await passwordFlow(sheet).getByRole('button', { name: 'Move it to the Recycle Bin' }).click()
	await expect(passwordFlow(sheet).getByRole('button', { name: 'Done' })).toBeVisible()
	expect(existsSync(file)).toBe(false)
	expect(zing.dialogs()).toContainEqual({ kind: 'recycle', url: 'my-export.csv' })
	await zing.close()
})

test('a passwords file from Dia imports into the vault and reports under the Dia row', async () => {
	const zing = await launchWithPasswords(true)
	const sheet = await openImport(zing)
	const file = join(scratchDir('zing-dia-passwords-'), 'Passwords.csv')
	writeFileSync(file, PASSWORDS_CSV)
	await zing.chrome.evaluate((f) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: [f] }), file)

	await macRow(sheet, 'Dia').getByRole('button', { name: 'Passwords file…' }).click()
	await expect(macRow(sheet, 'Dia').getByRole('status')).toContainText('Added 2, merged 0')
	await expect(macRow(sheet, 'Safari').getByRole('status')).toHaveCount(0)
	expect((await summaries(zing)).map((l) => l.username).sort()).toEqual(['ada', 'bob'])
	await zing.close()
})

const CHROME_DEFAULT = { ZING_DEFAULT_BROWSER: 'ChromeHTML' }
const isStartPage = (url: string): boolean => url === 'zing://home' || url === 'zing://home/'
const startPage = (zing: Launched) => pageWhere(zing.browser, (p) => isStartPage(p.url()))
const card = (start: Page) => start.locator('.nudge')
const personalChrome = () => localAppData([['Default', 'Personal', fixture('chromium-bookmarks.json')]])

test('the first New Tab offers the default browser and Import brings its bookmarks in', async () => {
	const zing = await launch(personalChrome(), CHROME_DEFAULT)
	const start = await startPage(zing)
	await expect(card(start).getByRole('heading')).toHaveText('Bring your stuff from Chrome?')
	await expect(card(start)).toContainText('and your favourites bar in "Personal".')
	for (const name of ['Import from Chrome', 'Other browser…', 'Not now']) await expect(card(start).getByRole('link', { name })).toBeVisible()

	await card(start).getByRole('link', { name: 'Import from Chrome' }).click()
	await expect(card(start).getByRole('heading')).toHaveText(/^Imported \d+ bookmarks?\.$/)
	await expect(card(start).getByRole('link', { name: 'Import passwords too' })).toBeVisible()
	await expect(card(start).getByRole('link', { name: 'Done' })).toBeVisible()

	const { bookmarks } = await library(zing)
	const imported = topFolder(bookmarks, 'Imported from Chrome (Personal)')
	expect(imported && 'children' in imported ? links(imported.children) : []).toContain('http://news.example/')

	const sheet = await openImport(zing)
	await sheet.getByRole('button', { name: 'Privacy', exact: true }).click()
	await expect(sheet.locator('.privacy-banner')).toHaveAttribute('data-traceless', 'false')
	await zing.close()
})

test('a slow scan of the other browsers does not delay the start page, and the card arrives when it ends', async () => {
	// The seam stands in for a big Chrome profile: the scan sleeps 6 s before it reads the root.
	const zing = await launch(personalChrome(), { ...CHROME_DEFAULT, ZING_LOCALAPPDATA_DELAY_MS: '6000' })
	const start = await startPage(zing)
	const painted = Date.now()
	await expect(start.locator('.tile').first()).toBeVisible({ timeout: 4000 })
	await expect(card(start)).toHaveCount(0)
	await expect(card(start).getByRole('heading')).toHaveText('Bring your stuff from Chrome?', { timeout: 30_000 })
	expect(Date.now() - painted).toBeGreaterThan(1000)
	await expect(card(start)).toHaveCount(1)
	await zing.close()
})

test('Not now hides the card on every later New Tab and after a restart', async () => {
	const root = personalChrome()
	const zing = await launch(root, CHROME_DEFAULT)
	const start = await startPage(zing)
	await card(start).getByRole('link', { name: 'Not now' }).click()
	await expect(card(start)).toHaveCount(0)

	await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))
	await expect.poll(() => pages(zing.browser).filter((p) => isStartPage(p.url())).length).toBe(2)
	for (const page of pages(zing.browser).filter((p) => isStartPage(p.url()))) await expect(page.locator('.tile').first()).toBeVisible()
	for (const page of pages(zing.browser).filter((p) => isStartPage(p.url()))) await expect(card(page)).toHaveCount(0)
	const userDataDir = zing.userDataDir
	await zing.close()

	const again = await launchZing({ userDataDir, env: { ZING_LOCALAPPDATA: root, ...CHROME_DEFAULT } })
	const fresh = await startPage(again)
	await expect(fresh.locator('.tile').first()).toBeVisible()
	await expect(card(fresh)).toHaveCount(0)
	await again.close()
})

test("Import passwords too opens Settings Import already waiting for Chrome's export", async () => {
	const zing = await launchZing({ userDataDir: passwordProfile(true), env: { ZING_LOCALAPPDATA: personalChrome(), ...CHROME_DEFAULT } })
	const start = await startPage(zing)
	await card(start).getByRole('link', { name: 'Import from Chrome' }).click()
	await expect(card(start).getByRole('heading')).toHaveText(/^Imported \d+ bookmarks?\.$/)
	await card(start).getByRole('link', { name: 'Import passwords too' }).click()

	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(passwordFlow(sheet)).toContainText('In Chrome, click Export passwords, confirm, and save. Zing will pick up the file.')
	await expect.poll(() => zing.dialogs()).toContainEqual(CHROME_LAUNCH)
	await zing.close()
})

test('with Zing as the default browser the card asks generically and Choose opens Settings Import', async () => {
	const zing = await launch(personalChrome(), { ZING_DEFAULT_BROWSER: 'ZingURL' })
	const start = await startPage(zing)
	await expect(card(start).getByRole('heading')).toHaveText('Bring your bookmarks from another browser?')
	// The host cancels this navigation and opens Settings without reloading the page, so no navigation commits for click to wait on.
	await card(start).getByRole('link', { name: 'Choose…' }).click({ noWaitAfter: true })
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(section(sheet)).toBeVisible()
	await expect(row(sheet, 'Chrome (Personal)')).toBeVisible()
	await zing.close()
})

test.describe('Import screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the import pictures')

	for (const mode of ['light', 'dark'] as const) {
		test(`the Import section, ${mode}`, async () => {
			test.setTimeout(180_000)
			mkdirSync(SHOTS, { recursive: true })
			const zing = await launch(
				localAppData(
					[
						['Default', 'Personal', fixture('chromium-bookmarks.json')],
						['Profile 1', 'Work', fixture('invalid.json')],
					],
					true,
				),
			)
			await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(zing.chrome)).dark).toBe(mode === 'dark')
			const sheet = await openImport(zing)
			await sheet.emulateMedia({ colorScheme: mode })
			await expect.poll(async () => (await viewAccent(sheet)).dark).toBe(mode === 'dark')
			await importFrom(sheet, 'Chrome (Personal)')
			await expect(row(sheet, 'Chrome (Personal)').getByRole('status')).toContainText('Added')
			await sheet.waitForTimeout(400)
			await sheet.getByRole('dialog', { name: 'Settings' }).screenshot({ path: join(SHOTS, `import-section-${mode}.png`) })
			await zing.close()
		})
	}

	for (const mode of ['light', 'dark'] as const) {
		test(`the From a Mac rows, ${mode}`, async () => {
			test.setTimeout(180_000)
			mkdirSync(SHOTS, { recursive: true })
			const zing = await launchWithPasswords(true)
			await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(zing.chrome)).dark).toBe(mode === 'dark')
			const sheet = await openImport(zing)
			await sheet.emulateMedia({ colorScheme: mode })
			await expect.poll(async () => (await viewAccent(sheet)).dark).toBe(mode === 'dark')
			await macRow(sheet, 'Arc').scrollIntoViewIfNeeded()
			await sheet.waitForTimeout(400)
			await sheet.getByRole('dialog', { name: 'Settings' }).screenshot({ path: join(SHOTS, `import-mac-${mode}.png`) })
			await zing.close()
		})

		test(`the password export fallback, ${mode}`, async () => {
			test.setTimeout(180_000)
			mkdirSync(SHOTS, { recursive: true })
			const zing = await launchWithPasswords(true)
			await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(zing.chrome)).dark).toBe(mode === 'dark')
			const sheet = await openImport(zing)
			await sheet.emulateMedia({ colorScheme: mode })
			await expect.poll(async () => (await viewAccent(sheet)).dark).toBe(mode === 'dark')
			await row(sheet, 'Chrome (Personal)').getByRole('button', { name: 'Import passwords' }).click()
			await expect(passwordFlow(sheet)).toContainText('If Chrome shows a new tab instead')
			await sheet.waitForTimeout(400)
			await sheet.getByRole('dialog', { name: 'Settings' }).screenshot({ path: join(SHOTS, `import-password-fallback-${mode}.png`) })
			await zing.close()
		})
	}

	for (const mode of ['light', 'dark'] as const) {
		test(`the New Tab import nudge, ${mode}`, async () => {
			test.setTimeout(180_000)
			mkdirSync(SHOTS, { recursive: true })
			const zing = await launch(personalChrome(), CHROME_DEFAULT)
			await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await expect.poll(async () => (await viewAccent(zing.chrome)).dark).toBe(mode === 'dark')
			const start = await startPage(zing)
			await start.emulateMedia({ colorScheme: mode })
			await expect(card(start).getByRole('heading')).toHaveText('Bring your stuff from Chrome?')
			await start.waitForTimeout(400)
			await start.screenshot({ path: join(SHOTS, `import-nudge-${mode}.png`) })
			await zing.close()
		})
	}
})

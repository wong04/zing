// Phase 6b PR A: the manage page in Settings, light and dark, into docs/screenshots/phase-6b: the empty state, a list, and
// the Windows Hello error. Skipped unless ZING_SCREENSHOTS=1, as it is run by hand.
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { viewAccent } from './theme-helpers'
import { launchZing, pageWhere, scratchDir, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots', 'phase-6b')
const MODES = ['light', 'dark'] as const

function profile(): string {
	const dir = scratchDir('zing-host-data-')
	writeFileSync(join(dir, 'settings.json'), JSON.stringify({ passwordAutofill: true }))
	return dir
}

const helloUnavailable = (zing: Launched): Promise<void> =>
	zing.chrome.evaluate(() => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'vault-hello', result: 'unavailable' }))

async function openSheet(zing: Launched, mode: (typeof MODES)[number]): Promise<Page> {
	await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
	await expect.poll(async () => (await viewAccent(zing.chrome)).dark).toBe(mode === 'dark')
	await zing.chrome.evaluate(() => window.zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	// Playwright pins every page it attaches to at light, over the host's override, and clearing that drops both.
	await sheet.emulateMedia({ colorScheme: mode })
	await expect.poll(async () => (await viewAccent(sheet)).dark).toBe(mode === 'dark')
	await sheet.getByRole('button', { name: 'Browsing' }).click()
	await sheet.getByRole('group', { name: 'Saved passwords' }).waitFor()
	return sheet
}

const shoot = async (sheet: Page, name: string, mode: string): Promise<void> => {
	await sheet.waitForTimeout(400)
	await sheet.getByRole('dialog', { name: 'Settings' }).screenshot({ path: join(OUT_DIR, `${name}-${mode}.png`) })
}

test.describe('Phase 6b screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the Phase 6b pictures')

	for (const mode of MODES) {
		test(`the manage page: empty, a list, and the Hello error, ${mode}`, async () => {
			test.setTimeout(180_000)
			mkdirSync(OUT_DIR, { recursive: true })
			const zing = await launchZing({ userDataDir: profile() })
			const sheet = await openSheet(zing, mode)
			await shoot(sheet, 'vault-empty', mode)

			for (const [origin, username, title] of [
				['https://github.com', 'ada@example.test', 'GitHub'],
				['https://accounts.example.com', 'ada.lovelace', 'Example account'],
				['https://news.example.net', 'reader42', ''],
			] as const) {
				await zing.chrome.evaluate((login) => window.zing.saveLogin(login), { origin, username, password: 'a correct horse battery', title, note: '' })
			}
			await expect(sheet.getByRole('list', { name: 'Saved logins' }).getByRole('listitem')).toHaveCount(3)
			await shoot(sheet, 'vault-list', mode)

			await helloUnavailable(zing)
			await sheet.getByRole('button', { name: 'Show password for GitHub' }).click()
			await expect(sheet.getByRole('group', { name: 'Saved passwords' }).getByRole('alert').last()).toContainText('no Windows Hello')
			await shoot(sheet, 'vault-hello-error', mode)
			await zing.close()
		})
	}
})

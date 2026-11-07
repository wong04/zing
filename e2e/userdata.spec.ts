// W-3: a user data file that can't be used never stops Zing starting. An invalid file is set aside as
// `<name>.corrupt-<stamp>`, Zing starts without it, and the first window says so. The recovery is logged
// as a warning, not recorded as an error, so these runs pass the harness's error check (e2e/zing.ts).
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, pageWhere, scratchDir } from './zing'

// Evaluated in the chrome renderer, where api.ts puts the bridge on window.
declare const window: { readonly zing: ZingApi }

const GARBAGE = '{ "this is": not json'

/** A scratch profile holding `files` (paths relative to it), as a previous run might have left it. */
function profileWith(files: Readonly<Record<string, string>>): string {
	const dir = scratchDir('zing-host-data-')
	for (const [name, content] of Object.entries(files)) {
		const path = join(dir, name)
		mkdirSync(join(path, '..'), { recursive: true })
		writeFileSync(path, content)
	}
	return dir
}

const kept = (dir: string, name: string): string[] => readdirSync(dir).filter((file) => file.startsWith(`${name}.corrupt-`))

test('a garbage settings.json is set aside, and Zing starts with the defaults and says so', async () => {
	const userDataDir = profileWith({ 'settings.json': GARBAGE })
	const zing = await launchZing({ userDataDir })
	await expect(zing.chrome.locator('[role="tab"]')).toHaveCount(1)
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(overlay.getByText(/couldn't read your settings, so it started with the defaults/)).toBeVisible()

	const [corrupt, ...others] = kept(userDataDir, 'settings.json')
	expect(others).toEqual([])
	expect(readFileSync(join(userDataDir, corrupt!), 'utf8')).toBe(GARBAGE)
	await expect(overlay.getByText(corrupt!, { exact: false })).toBeVisible()

	// The bad file is out of the way, so this launch's changes are saved again.
	await zing.chrome.evaluate(() => window.zing.setSetting('searchEngine', 'duckduckgo'))
	await zing.close()
	expect(JSON.parse(readFileSync(join(userDataDir, 'settings.json'), 'utf8')).searchEngine).toBe('duckduckgo')
})

test('a garbage library.json is set aside, and Zing starts with an empty library and says so', async () => {
	const userDataDir = profileWith({ 'library.json': GARBAGE })
	const zing = await launchZing({ userDataDir })
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(overlay.getByText(/couldn't read your bookmarks and reading list, so it started with an empty library/)).toBeVisible()
	expect(kept(userDataDir, 'library.json')).toHaveLength(1)
	await zing.close()
})

test('a garbage account file is set aside, and Zing starts without an account and says so', async () => {
	const userDataDir = profileWith({ [join('account', 'account.json')]: GARBAGE })
	const zing = await launchZing({ userDataDir })
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(overlay.getByText(/couldn't read your Zing account, so it started without it/)).toBeVisible()
	expect(kept(join(userDataDir, 'account'), 'account.json')).toHaveLength(1)
	await zing.close()
})

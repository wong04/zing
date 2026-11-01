import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { expect, test } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, layers, pages, pageWhere } from './zing'

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

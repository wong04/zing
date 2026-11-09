// Sync's Google sign-in (Task 11). No test reaches Google or Railway: the real sync server (server/Zing.Server) runs on this PC with
// its fake Google issuer (ZING_SERVER_FAKE_GOOGLE=1 in the Staging environment, as its own tests and CI run it), and in an e2e run
// Zing's Google sign-in is a stand-in that asks that issuer for an ID token (ZING_E2E_SYNC_SERVER), so no browser opens.
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import { launchZing, pageWhere, scratchDir, test, type Launched } from './zing'
import type { ZingApi } from '../src/shared/ipc'

declare const window: { readonly zing: ZingApi }

const ROOT = join(import.meta.dirname, '..')
const SERVER_PROJECT = join(ROOT, 'server', 'Zing.Server')
const SERVER_DLL = join(SERVER_PROJECT, 'bin', 'Release', 'net9.0', 'Zing.Server.dll')
const SUB = 'sub-e2e-sync'
const EMAIL = `${SUB}@example.test`
// Each test that turns sync on is its own Google account: the server keeps one vault's keys per account, for the whole run.
const TURN_ON_SUBS = ['sub-e2e-turn-on', 'sub-e2e-weak'] as const
// The two-PC sync test's Google account (Task 13).
const TWO_PCS_SUB = 'sub-e2e-two-pcs'
// Restore from sync, the recovery key, a password changed on another PC, and a conflict and a delete (Task 14).
const RESTORE_SUBS = ['sub-e2e-restore', 'sub-e2e-recovery', 'sub-e2e-changed', 'sub-e2e-conflict'] as const
// Turn off sync on this PC (Ruling 68).
const TURN_OFF_SUB = 'sub-e2e-turn-off'

let server: ChildProcess | undefined
let serverUrl = ''
let serverDir = ''
let serverLog = ''

const LISTENING = /Now listening on: (http:\/\/127\.0\.0\.1:\d+)/

/** Only what the server needs from the shell: its own ZING_* and ASPNETCORE_* variables would change the run. */
function serverEnvironment(): NodeJS.ProcessEnv {
	const inherited = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(ZING_|ASPNETCORE_|DOTNET_ENVIRONMENT)/i.test(key)))
	return {
		...inherited,
		// Port 0: the server binds a free port itself and logs it, so nothing can take it between a probe and the bind.
		ASPNETCORE_URLS: 'http://127.0.0.1:0',
		ASPNETCORE_ENVIRONMENT: 'Staging',
		ZING_SERVER_FAKE_GOOGLE: '1',
		ZING_SESSION_KEY: randomBytes(32).toString('base64'),
		ZING_ALLOWED_SUBS: [SUB, ...TURN_ON_SUBS, TWO_PCS_SUB, ...RESTORE_SUBS, TURN_OFF_SUB].join(','),
		ZING_DB_PATH: join(serverDir, 'zing.db'),
		// Every PC of the run is 127.0.0.1, so one per-IP budget: the default 60 a minute is a production limit (its own tests cover it).
		ZING_RATE_IP_PER_MINUTE: '1000',
		ZING_RATE_USER_PER_MINUTE: '1000',
	}
}

test.beforeAll(async () => {
	test.setTimeout(180_000)
	try {
		execFileSync('dotnet', ['build', SERVER_PROJECT, '-c', 'Release', '--nologo', '-v', 'quiet'], { stdio: 'pipe', timeout: 170_000 })
	} catch (error) {
		const output = error as { stdout?: Buffer; stderr?: Buffer }
		throw new Error(`Building the sync server failed:\n${output.stdout?.toString() ?? ''}${output.stderr?.toString() ?? ''}`, { cause: error })
	}
	if (!existsSync(SERVER_DLL)) throw new Error(`No server build at ${SERVER_DLL}`)
	serverDir = mkdtempSync(join(tmpdir(), 'zing-sync-server-'))
	server = spawn('dotnet', [SERVER_DLL], { env: serverEnvironment(), stdio: ['ignore', 'pipe', 'pipe'] })
	server.stdout?.on('data', (chunk: Buffer) => (serverLog += chunk.toString()))
	server.stderr?.on('data', (chunk: Buffer) => (serverLog += chunk.toString()))
	await expect.poll(() => LISTENING.test(serverLog), { timeout: 60_000, message: "the local sync server didn't say where it listens" }).toBe(true)
	serverUrl = LISTENING.exec(serverLog)![1]!
	const health = await fetch(`${serverUrl}/health`)
	if (health.status !== 200) throw new Error(`The local sync server answered /health with ${health.status}:\n${serverLog}`)
})

test.afterAll(async () => {
	if (server && server.exitCode === null) {
		const exited = new Promise((resolve) => server!.once('exit', resolve))
		server.kill()
		await exited
	}
	if (serverDir) rmSync(serverDir, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 })
})

const syncEnv = (sub: string = SUB): Record<string, string> => ({ ZING_E2E_SYNC_SERVER: serverUrl, ZING_E2E_SYNC_SUB: sub })

/** A profile with the vault on: Turn on sync is part of the saved-passwords page. */
function vaultProfile(): string {
	const dir = scratchDir('zing-host-data-')
	mkdirSync(dir, { recursive: true })
	writeFileSync(join(dir, 'settings.json'), JSON.stringify({ passwordAutofill: true }))
	return dir
}

async function openSync(zing: Launched): Promise<Page> {
	await zing.chrome.evaluate(() => (window as unknown as { zing: { runCommand(id: string): Promise<void> } }).zing.runCommand('app.settings'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await sheet.getByRole('button', { name: 'Browsing' }).click()
	return sheet
}

const syncGroup = (sheet: Page) => sheet.getByRole('group', { name: 'Saved passwords' }).getByRole('group', { name: 'Sync' })

const syncFile = (zing: Launched): string => join(zing.userDataDir, 'vault', 'sync.json')

test('Turn on sync signs in against the sync server, stays signed in after a restart, and Sign out forgets it', async () => {
	const first = await launchZing({ userDataDir: vaultProfile(), env: syncEnv() })
	const sheet = await openSync(first)

	await syncGroup(sheet).getByRole('button', { name: 'Turn on sync' }).click()

	await expect(syncGroup(sheet)).toContainText(`Signed in as ${EMAIL}.`)
	const stored = readFileSync(syncFile(first), 'utf8')
	const account = (JSON.parse(stored) as { account: { userId: string; email: string; sealedRefresh: string } }).account
	expect(account.email).toBe(EMAIL)
	expect(account.userId).not.toBe('')
	// Sealed with DPAPI: what's on disk is not the token the server issued (those are base64url, with no padding).
	expect(account.sealedRefresh.length).toBeGreaterThan(100)
	await first.close()

	const again = await launchZing({ userDataDir: first.userDataDir, env: syncEnv() })
	const reopened = await openSync(again)
	await expect(syncGroup(reopened)).toContainText(`Signed in as ${EMAIL}.`)

	await syncGroup(reopened).getByRole('button', { name: 'Sign out' }).click()

	// Signed in, the setup form has a Turn on sync button of its own; signed out, only the row's is left.
	await expect(syncGroup(reopened)).not.toContainText('Signed in as')
	await expect(syncGroup(reopened).getByRole('button', { name: 'Turn on sync' })).toBeVisible()
	expect((JSON.parse(readFileSync(syncFile(again), 'utf8')) as { account: unknown }).account).toBeNull()
	await again.close()
})

test('without a sync server, as in a build without a Google client, Turn on sync says sync is not set up and opens nothing', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile() })
	const sheet = await openSync(zing)

	await syncGroup(sheet).getByRole('button', { name: 'Turn on sync' }).click()

	await expect(syncGroup(sheet)).toContainText("Sync isn't set up in this build of Zing")
	await expect(syncGroup(sheet).getByRole('button', { name: 'Turn on sync' })).toBeEnabled()
	expect(existsSync(syncFile(zing))).toBe(false)
	await zing.close()
})

// Turning sync on (Task 12): the Zing password wraps the vault's keys, and the recovery key is shown until it's confirmed saved.
// Windows Hello is the e2e stand-in (vault-hello), never a real prompt.

/** A five-word passphrase the generator made (src/shared/vault/passwordStrength.vectors.json). */
const STRONG = 'teaching-focused-onward-activist-steel'
const RECOVERY_KEY = /^[A-Z2-7]{4}(-[A-Z2-7]{4}){5}-[A-Z2-7]{2}$/

type TestMessage = { action: 'vault-hello'; result: 'pass' | 'cancel' }

const hello = (zing: Launched, result: TestMessage['result']): Promise<unknown> =>
	zing.chrome.evaluate(
		(m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', ...m }),
		{ action: 'vault-hello', result } satisfies TestMessage,
	)

async function signUp(zing: Launched, password: string): Promise<void> {
	const outcome = await zing.chrome.evaluate((p) => window.zing.signUp('ada', 'Ada Lovelace', 'ada.profile@example.test', p), password)
	expect(outcome.status).toBe('ok')
}

const keysFile = (zing: Launched): string => join(zing.userDataDir, 'vault', 'keys.bin')

type StoredSync = { sync: { userId: string; version: number; recoveryConfirmed: boolean } | null }

const recoveryScreen = (sheet: Page) => syncGroup(sheet).getByRole('alertdialog', { name: 'Save your recovery key' })

test('turning on sync shows the recovery key, and nothing else of the vault, until it is confirmed saved, even after a restart, and then the account can’t be deleted', async () => {
	const first = await launchZing({ userDataDir: vaultProfile(), env: syncEnv(TURN_ON_SUBS[0]) })
	await signUp(first, STRONG)
	const sheet = await openSync(first)
	await syncGroup(sheet).getByRole('button', { name: 'Turn on sync' }).click()
	await expect(syncGroup(sheet)).toContainText(`Signed in as ${TURN_ON_SUBS[0]}@example.test.`)

	await syncGroup(sheet).getByLabel('Zing password').fill(STRONG)
	await syncGroup(sheet).getByRole('form', { name: 'Turn on sync' }).getByRole('button', { name: 'Turn on sync' }).click()

	const screen = recoveryScreen(sheet)
	await expect(screen.getByLabel('Recovery key', { exact: true })).toHaveText(RECOVERY_KEY)
	const recovery = (await screen.getByLabel('Recovery key', { exact: true }).textContent())!
	await expect(sheet.getByRole('button', { name: 'Import CSV' })).toHaveCount(0)
	await expect(syncGroup(sheet).getByRole('button', { name: 'Sign out' })).toHaveCount(0)
	await expect(screen.getByRole('button', { name: 'Continue' })).toBeDisabled()
	const stored = readFileSync(keysFile(first), 'utf8')
	expect((JSON.parse(stored) as StoredSync).sync).toMatchObject({ version: 1, recoveryConfirmed: false })
	expect(stored).not.toContain(recovery)
	expect(stored).not.toContain(recovery.replaceAll('-', ''))
	await first.close()

	const again = await launchZing({ userDataDir: first.userDataDir, env: syncEnv(TURN_ON_SUBS[0]) })
	const reopened = await openSync(again)
	const blocked = recoveryScreen(reopened)
	await expect(blocked.getByRole('button', { name: 'Show recovery key' })).toBeVisible()
	await expect(reopened.getByRole('button', { name: 'Import CSV' })).toHaveCount(0)
	await expect(blocked.getByRole('button', { name: 'Continue' })).toBeDisabled()

	await hello(again, 'cancel')
	await blocked.getByRole('button', { name: 'Show recovery key' }).click()
	await expect(blocked.getByRole('alert')).toBeVisible()
	await expect(blocked.getByLabel('Recovery key', { exact: true })).toHaveCount(0)
	await expect(blocked).not.toContainText(recovery)

	await hello(again, 'pass')
	await blocked.getByRole('button', { name: 'Show recovery key' }).click()
	await expect(blocked.getByLabel('Recovery key', { exact: true })).toHaveText(recovery)
	await expect(blocked.getByRole('button', { name: 'Continue' })).toBeDisabled()
	await blocked.getByRole('checkbox', { name: 'I’ve saved or printed my recovery key' }).check()
	await blocked.getByRole('button', { name: 'Continue' }).click()

	await expect(blocked).toHaveCount(0)
	await expect(syncGroup(reopened)).toContainText(`Sync is on. Signed in as ${TURN_ON_SUBS[0]}@example.test.`)
	await expect(reopened.getByRole('button', { name: 'Import CSV' })).toBeVisible()
	expect((JSON.parse(readFileSync(keysFile(again), 'utf8')) as StoredSync).sync).toMatchObject({ recoveryConfirmed: true })

	// With sync on, the account password is the sync password, so the account isn't deleted (final review I2).
	await reopened.getByRole('button', { name: 'Account' }).click()
	await reopened.getByRole('button', { name: 'Delete…' }).click()
	await reopened.getByRole('button', { name: 'Delete', exact: true }).click()
	await expect(reopened.getByText('Sync is on for this PC. Turn off sync on this PC first.')).toBeVisible()
	await expect(reopened.getByRole('button', { name: 'Sign Out' })).toBeVisible()
	await again.close()
})

test('a Zing password too weak for sync is changed to a suggested passphrase in the same flow', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(), env: syncEnv(TURN_ON_SUBS[1]) })
	await signUp(zing, 'correct horse')
	const sheet = await openSync(zing)
	await syncGroup(sheet).getByRole('button', { name: 'Turn on sync' }).click()
	await expect(syncGroup(sheet)).toContainText(`Signed in as ${TURN_ON_SUBS[1]}@example.test.`)

	await syncGroup(sheet).getByLabel('Zing password').fill('correct horse')
	await expect(syncGroup(sheet).getByRole('status').filter({ hasText: 'Sync needs about 60 bits, such as five random words.' })).toBeVisible()
	await syncGroup(sheet).getByRole('form', { name: 'Turn on sync' }).getByRole('button', { name: 'Turn on sync' }).click()

	const stronger = syncGroup(sheet).getByRole('form', { name: 'Choose a stronger password' })
	await stronger.getByRole('button', { name: 'Suggest a passphrase' }).click()
	const suggested = (await stronger.getByLabel('Suggested passphrase').textContent())!
	expect(suggested.split('-')).toHaveLength(5)
	await stronger.getByRole('button', { name: 'Use it' }).click()
	await expect(stronger.getByRole('status').filter({ hasText: 'Strong enough for sync.' })).toBeVisible()
	await stronger.getByRole('button', { name: 'Change password and turn on sync' }).click()

	await expect(recoveryScreen(sheet).getByLabel('Recovery key', { exact: true })).toHaveText(RECOVERY_KEY)
	const signedOut = await zing.chrome.evaluate(() => window.zing.signOut())
	expect(signedOut.status).toBe('ok')
	expect((await zing.chrome.evaluate(() => window.zing.signIn('correct horse'))).status).toBe('rejected')
	expect((await zing.chrome.evaluate((p) => window.zing.signIn(p), suggested)).status).toBe('ok')
	await zing.close()
})

// The sync engine (Task 13): two Zing profiles, each its own PC, share a login through the local server. Sync itself needs no
// Windows Hello; turning it on, restoring and saving a password use the vault-hello stand-in. The second PC joins by restoring
// from sync with the Zing password (Task 14).

/** Restore from sync through the host, as the gate's Restore from sync does it, on a profile with no account. */
async function restoreWithPassword(zing: Launched, password: string): Promise<void> {
	expect((await zing.chrome.evaluate(() => window.zing.signInToRestore())).signedIn).toBe(true)
	expect((await zing.chrome.evaluate((p) => window.zing.restoreWithPassword(p), password)).status).toBe('ok')
}

const titleOf = (zing: Launched, id: string): Promise<string | undefined> =>
	zing.chrome.evaluate(async (loginId) => (await window.zing.vaultSummaries()).find((s) => s.id === loginId)?.title, id)

test('a login saved on one PC reaches the other, and an edit made there comes back', async () => {
	test.setTimeout(240_000)
	const a = await launchZing({ userDataDir: vaultProfile(), env: syncEnv(TWO_PCS_SUB) })
	await signUp(a, STRONG)
	await hello(a, 'pass')
	const id = await a.chrome.evaluate(() => window.zing.saveLogin({ origin: 'https://bank.example.test', username: 'ada', password: 'first-password', title: 'Bank', note: '' }))
	expect((await a.chrome.evaluate(() => window.zing.signInToSync())).signedIn).toBe(true)
	await a.chrome.evaluate((p) => window.zing.turnOnSync(p), STRONG)
	const on = await a.chrome.evaluate(() => window.zing.confirmRecoverySaved())
	expect(on).toMatchObject({ on: true, unsent: 0 })
	expect(on).not.toHaveProperty('error')
	await a.close()

	const b = await launchZing({ env: syncEnv(TWO_PCS_SUB) })
	await restoreWithPassword(b, STRONG)
	expect(await b.chrome.evaluate(() => window.zing.getSyncState())).toMatchObject({ signedIn: true, on: true })
	await expect.poll(() => titleOf(b, id), { timeout: 30_000, message: "B's sync didn't bring A's login" }).toBe('Bank')

	await b.chrome.evaluate((loginId) => window.zing.updateLogin(loginId, { title: 'Edited on B', username: 'ada', note: '' }), id)
	await expect
		.poll(() => b.chrome.evaluate(() => window.zing.getSyncState()), { timeout: 30_000, message: "B's edit didn't upload" })
		.toMatchObject({ on: true, unsent: 0 })
	expect(await b.chrome.evaluate(() => window.zing.getSyncState())).not.toHaveProperty('error')
	await b.close()

	const again = await launchZing({ userDataDir: a.userDataDir, env: syncEnv(TWO_PCS_SUB) })
	await expect.poll(() => titleOf(again, id), { timeout: 30_000, message: "A's sync at start didn't bring B's edit" }).toBe('Edited on B')
	const sheet = await openSync(again)
	await expect(syncGroup(sheet)).toContainText(`Sync is on. Signed in as ${TWO_PCS_SUB}@example.test.`)
	await again.close()
})

// A new PC, a forgotten password, and a password changed on another PC (Task 14). The new PC starts from an empty profile, at the
// account gate, as a real first start does.

/** Another generated five-word passphrase (src/shared/vault/passwordStrength.vectors.json). */
const NEXT_STRONG = 'labour-muzzle-medici-rushes-fiction'

/** A PC with an account, two logins and sync turned on for `sub`, its recovery key confirmed saved. */
async function turnOnWithLogins(sub: string): Promise<{ zing: Launched; recovery: string; bank: string }> {
	const zing = await launchZing({ userDataDir: vaultProfile(), env: syncEnv(sub) })
	await signUp(zing, STRONG)
	const bank = await zing.chrome.evaluate(() => window.zing.saveLogin({ origin: 'https://bank.example.test', username: 'ada', password: 'bank-password', title: 'Bank', note: '' }))
	await zing.chrome.evaluate(() => window.zing.saveLogin({ origin: 'https://mail.example.test', username: 'ada.mail', password: 'mail-password', title: 'Mail', note: '' }))
	expect((await zing.chrome.evaluate(() => window.zing.signInToSync())).signedIn).toBe(true)
	const { recoveryKey } = await zing.chrome.evaluate((p) => window.zing.turnOnSync(p), STRONG)
	expect(await zing.chrome.evaluate(() => window.zing.confirmRecoverySaved())).toMatchObject({ on: true, unsent: 0 })
	return { zing, recovery: recoveryKey, bank }
}

test('Turn off sync on this PC keeps the logins, forgets the sync sign-in and keys, and then the account can be deleted', async () => {
	const { zing, bank } = await turnOnWithLogins(TURN_OFF_SUB)
	const sheet = await openSync(zing)
	await expect(syncGroup(sheet)).toContainText(`Sync is on. Signed in as ${TURN_OFF_SUB}@example.test. Changes made on your other PCs come in when Zing starts, then every 5 minutes.`)
	// Styled as the row's other buttons are (Task 15 text check).
	const signOutClass = await syncGroup(sheet).getByRole('button', { name: 'Sign out' }).getAttribute('class')
	await expect(syncGroup(sheet).getByRole('button', { name: 'Turn off sync on this PC' })).toHaveAttribute('class', signOutClass!)

	await syncGroup(sheet).getByRole('button', { name: 'Turn off sync on this PC' }).click()
	const confirm = syncGroup(sheet).getByRole('alertdialog', { name: 'Turn off sync on this PC' })
	await expect(confirm).toContainText('This PC stops syncing and keeps its logins. Your other PCs and the server copy aren’t changed.')
	await confirm.getByRole('button', { name: 'Turn off', exact: true }).click()

	await expect(syncGroup(sheet).getByRole('button', { name: 'Turn on sync' })).toBeVisible()
	await expect(syncGroup(sheet).getByRole('button', { name: 'Turn off sync on this PC' })).toHaveCount(0)
	expect(await zing.chrome.evaluate(() => window.zing.getSyncState())).toMatchObject({ signedIn: false, on: false, keysHere: false })
	expect((JSON.parse(readFileSync(keysFile(zing), 'utf8')) as StoredSync).sync).toBeNull()
	expect(await titles(zing)).toEqual(['Bank', 'Mail'])
	await hello(zing, 'pass')
	expect(await zing.chrome.evaluate((id) => window.zing.revealPassword(id), bank)).toBe('bank-password')

	await sheet.getByRole('button', { name: 'Account' }).click()
	await sheet.getByRole('button', { name: 'Delete…' }).click()
	await sheet.getByRole('button', { name: 'Delete', exact: true }).click()
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getAccount())).hasAccount, { message: 'the account was not deleted' }).toBe(false)
	// Behind the account gate no command runs, the harness's window.close among them.
	expect((await zing.chrome.evaluate(() => window.zing.continueAsGuest())).status).toBe('ok')
	await zing.close()
})

/** The account gate shows, as on a real start: the harness skips it otherwise. */
const gated = { ZING_SKIP_ACCOUNT_GATE: '' }

const WRONG_PASSWORD = "That password doesn't match."

const surfaceOf = (zing: Launched): Promise<Page> => pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))

const titles = async (zing: Launched): Promise<string[]> => (await zing.chrome.evaluate(() => window.zing.vaultSummaries())).map((s) => s.title).sort()

const isGated = async (zing: Launched): Promise<boolean> => (await zing.chrome.evaluate(() => window.zing.getShell())).gated

const summaryOf = async (zing: Launched, id: string) => (await zing.chrome.evaluate(() => window.zing.vaultSummaries())).find((s) => s.id === id)

/** The gate's Restore from sync, signed in with Google. */
async function startRestore(zing: Launched, sub: string): Promise<Locator> {
	const gate = await surfaceOf(zing)
	await gate.getByRole('button', { name: 'Restore from sync' }).click()
	const restore = gate.getByRole('form', { name: 'Restore from sync' })
	await restore.getByRole('button', { name: 'Sign in with Google' }).click()
	await expect(restore).toContainText(`Signed in as ${sub}@example.test.`)
	return restore
}

test('restoring from sync on a new PC refuses a wrong password and makes nothing, then the password brings back every login', async () => {
	test.setTimeout(240_000)
	const sub = RESTORE_SUBS[0]
	const { zing: a, bank } = await turnOnWithLogins(sub)
	await a.close()

	const b = await launchZing({ env: { ...syncEnv(sub), ...gated } })
	const restore = await startRestore(b, sub)
	await restore.getByLabel('Zing password', { exact: true }).fill(NEXT_STRONG)
	await restore.getByRole('button', { name: 'Restore', exact: true }).click()

	await expect(restore).toContainText(WRONG_PASSWORD)
	expect(await b.chrome.evaluate(() => window.zing.getAccount())).toMatchObject({ hasAccount: false })
	expect(existsSync(keysFile(b))).toBe(false)
	expect(await isGated(b)).toBe(true)

	await restore.getByLabel('Zing password', { exact: true }).fill(STRONG)
	await restore.getByRole('button', { name: 'Restore', exact: true }).click()

	await expect.poll(() => isGated(b)).toBe(false)
	expect(await b.chrome.evaluate(() => window.zing.getAccount())).toMatchObject({
		hasAccount: true,
		signedIn: true,
		profile: { username: 'ada', displayName: 'Ada Lovelace', email: 'ada.profile@example.test' },
	})
	await expect.poll(() => titles(b), { timeout: 30_000, message: "B's sync didn't bring A's logins" }).toEqual(['Bank', 'Mail'])
	expect(await b.chrome.evaluate((id) => window.zing.revealPassword(id), bank)).toBe('bank-password')
	expect(await b.chrome.evaluate(() => window.zing.getSyncState())).toMatchObject({ signedIn: true, on: true, recoveryPending: false })
	await b.close()
})

test('restoring with the recovery key asks for a new password at once, and the old password stops working on the next restore', async () => {
	test.setTimeout(240_000)
	const sub = RESTORE_SUBS[1]
	const { zing: a, recovery } = await turnOnWithLogins(sub)
	await a.close()

	const b = await launchZing({ env: { ...syncEnv(sub), ...gated } })
	const restore = await startRestore(b, sub)
	await restore.getByRole('radio', { name: 'Recovery key' }).click()
	await restore.getByLabel('Recovery key', { exact: true }).fill(recovery.toLowerCase().replaceAll('-', ' '))
	await restore.getByRole('button', { name: 'Restore', exact: true }).click()

	await expect(restore.getByRole('heading', { name: 'Choose a new Zing password' })).toBeVisible()
	expect(await b.chrome.evaluate(() => window.zing.getAccount())).toMatchObject({ hasAccount: false })
	expect(await isGated(b)).toBe(true)
	await restore.getByLabel('New Zing password').fill(NEXT_STRONG)
	await restore.getByLabel('New password again').fill(NEXT_STRONG)
	await restore.getByRole('button', { name: 'Set password' }).click()

	await expect.poll(() => isGated(b)).toBe(false)
	expect(await b.chrome.evaluate(() => window.zing.getAccount())).toMatchObject({ hasAccount: true, signedIn: true, profile: { username: 'ada' } })
	await expect.poll(() => titles(b), { timeout: 30_000, message: "B's sync didn't bring A's logins" }).toEqual(['Bank', 'Mail'])
	await b.close()

	const c = await launchZing({ env: syncEnv(sub) })
	expect((await c.chrome.evaluate(() => window.zing.signInToRestore())).signedIn).toBe(true)
	expect(await c.chrome.evaluate((p) => window.zing.restoreWithPassword(p), STRONG)).toMatchObject({ status: 'rejected', code: 'wrong-password' })
	expect(await c.chrome.evaluate((p) => window.zing.restoreWithPassword(p), NEXT_STRONG)).toMatchObject({ status: 'ok' })
	await c.close()
})

test('a password changed on one PC makes the other ask for the new one once, at its next start', async () => {
	test.setTimeout(300_000)
	const sub = RESTORE_SUBS[2]
	const { zing: a } = await turnOnWithLogins(sub)
	const b = await launchZing({ env: syncEnv(sub) })
	await restoreWithPassword(b, STRONG)
	await b.close()
	expect(await a.chrome.evaluate(([current, next]) => window.zing.changePassword(current!, next!), [STRONG, NEXT_STRONG])).toMatchObject({ status: 'ok' })
	await a.close()

	// B's sync at start finds the newer keys and says so; this start doesn't lock.
	const noticed = await launchZing({ userDataDir: b.userDataDir, env: syncEnv(sub) })
	const sheet = await openSync(noticed)
	await expect(syncGroup(sheet)).toContainText('Your Zing password was changed on another PC.')
	expect(await isGated(noticed)).toBe(false)
	await noticed.close()

	const asked = await launchZing({ userDataDir: b.userDataDir, env: { ...syncEnv(sub), ...gated } })
	const gate = await surfaceOf(asked)
	await expect(gate.getByText('Your Zing password was changed on another PC. Enter the new one.')).toBeVisible()
	await gate.getByLabel('Password', { exact: true }).fill(STRONG)
	await gate.getByRole('button', { name: 'Sign In' }).click()
	await expect(gate.getByText(WRONG_PASSWORD)).toBeVisible()
	expect(await isGated(asked)).toBe(true)
	await gate.getByLabel('Password', { exact: true }).fill(NEXT_STRONG)
	await gate.getByRole('button', { name: 'Sign In' }).click()
	await expect.poll(() => isGated(asked)).toBe(false)
	await asked.close()

	const once = await launchZing({ userDataDir: b.userDataDir, env: { ...syncEnv(sub), ...gated } })
	expect(await once.chrome.evaluate(() => window.zing.getAccount())).toMatchObject({ signedIn: true, passwordChangedElsewhere: false })
	expect(await isGated(once)).toBe(false)
	await once.close()
})

// The spec's two PCs syncing "including a conflict and a delete" (M11, Task 13 review).
test('an edit made on each PC while one was offline merges field by field, and a delete reaches the other PC', async () => {
	test.setTimeout(300_000)
	const sub = RESTORE_SUBS[3]
	const { zing: a, bank } = await turnOnWithLogins(sub)
	await a.close()
	const b = await launchZing({ env: syncEnv(sub) })
	await restoreWithPassword(b, STRONG)
	await expect.poll(() => titleOf(b, bank), { timeout: 30_000, message: "B's sync didn't bring A's login" }).toBe('Bank')

	// A edits the note with no sync server to reach (nothing listens on port 9), so its change waits.
	const offline = await launchZing({ userDataDir: a.userDataDir, env: { ZING_E2E_SYNC_SERVER: 'http://127.0.0.1:9', ZING_E2E_SYNC_SUB: sub } })
	await offline.chrome.evaluate((id) => window.zing.updateLogin(id, { title: 'Bank', username: 'ada', note: 'Note from A' }), bank)
	await expect.poll(() => offline.chrome.evaluate(() => window.zing.getSyncState()), { timeout: 30_000 }).toMatchObject({ error: 'unreachable', unsent: 1 })
	await offline.close()

	await b.chrome.evaluate((id) => window.zing.updateLogin(id, { title: 'Edited on B', username: 'ada', note: '' }), bank)
	await expect.poll(() => b.chrome.evaluate(() => window.zing.getSyncState()), { timeout: 30_000, message: "B's edit didn't upload" }).toMatchObject({ on: true, unsent: 0 })
	await b.close()

	const online = await launchZing({ userDataDir: a.userDataDir, env: syncEnv(sub) })
	await expect.poll(() => summaryOf(online, bank), { timeout: 30_000, message: "A didn't merge B's edit with its own" }).toMatchObject({ title: 'Edited on B', note: 'Note from A' })
	await expect.poll(() => online.chrome.evaluate(() => window.zing.getSyncState()), { timeout: 30_000 }).toMatchObject({ on: true, unsent: 0 })
	expect(await online.chrome.evaluate(() => window.zing.getSyncState())).not.toHaveProperty('error')
	await online.chrome.evaluate((id) => window.zing.deleteLogin(id), bank)
	await expect.poll(() => online.chrome.evaluate(() => window.zing.getSyncState()), { timeout: 30_000, message: "A's delete didn't upload" }).toMatchObject({ unsent: 0 })
	await online.close()

	const again = await launchZing({ userDataDir: b.userDataDir, env: syncEnv(sub) })
	await expect.poll(async () => (await summaryOf(again, bank))?.deletedAt != null, { timeout: 30_000, message: "B's sync didn't bring A's delete" }).toBe(true)
	expect(await summaryOf(again, bank)).toMatchObject({ title: 'Edited on B', note: 'Note from A' })
	await again.close()
})

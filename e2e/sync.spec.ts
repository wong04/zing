// Sync's Google sign-in (Task 11). No test reaches Google or Railway: the real sync server (server/Zing.Server) runs on this PC with
// its fake Google issuer (ZING_SERVER_FAKE_GOOGLE=1 in the Staging environment, as its own tests and CI run it), and in an e2e run
// Zing's Google sign-in is a stand-in that asks that issuer for an ID token (ZING_E2E_SYNC_SERVER), so no browser opens.
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
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
		ZING_ALLOWED_SUBS: [SUB, ...TURN_ON_SUBS].join(','),
		ZING_DB_PATH: join(serverDir, 'zing.db'),
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

test('turning on sync shows the recovery key, and nothing else of the vault, until it is confirmed saved, even after a restart', async () => {
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
	await again.close()
})

test('a Zing password too weak for sync is changed to a suggested passphrase in the same flow', async () => {
	const zing = await launchZing({ userDataDir: vaultProfile(), env: syncEnv(TURN_ON_SUBS[1]) })
	await signUp(zing, 'correct horse')
	const sheet = await openSync(zing)
	await syncGroup(sheet).getByRole('button', { name: 'Turn on sync' }).click()
	await expect(syncGroup(sheet)).toContainText(`Signed in as ${TURN_ON_SUBS[1]}@example.test.`)

	await syncGroup(sheet).getByLabel('Zing password').fill('correct horse')
	await expect(syncGroup(sheet).getByRole('status').filter({ hasText: 'Sync needs a stronger password.' })).toBeVisible()
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

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

const ROOT = join(import.meta.dirname, '..')
const SERVER_PROJECT = join(ROOT, 'server', 'Zing.Server')
const SERVER_DLL = join(SERVER_PROJECT, 'bin', 'Release', 'net9.0', 'Zing.Server.dll')
const SUB = 'sub-e2e-sync'
const EMAIL = `${SUB}@example.test`

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
		ZING_ALLOWED_SUBS: SUB,
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

const syncEnv = (): Record<string, string> => ({ ZING_E2E_SYNC_SERVER: serverUrl, ZING_E2E_SYNC_SUB: SUB })

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

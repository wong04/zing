import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, rmSync, utimesSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { afterEach, beforeEach, expect, test } from 'vitest'
import { takeRunLock } from './run-lock'

const lockModule = pathToFileURL(resolve('scripts/run-lock.ts')).href

let scratch: string
let lock: string

beforeEach(() => {
	scratch = mkdtempSync(join(tmpdir(), 'zing-run-lock-'))
	lock = join(scratch, 'zing-pw.lock')
})

afterEach(() => rmSync(scratch, { recursive: true, force: true }))

type Run = { start: number; end: number }

// A separate Node process that takes the lock, holds it as a test run would, then ends in the given way.
function run(holdMs: number, ending: string): Promise<Run & { code: number | null }> {
	const script = `
		const { takeRunLock } = await import(${JSON.stringify(lockModule)})
		const release = await takeRunLock(${JSON.stringify(lock)}, { pollMs: 50 })
		const start = Date.now()
		await new Promise((r) => setTimeout(r, ${holdMs}))
		console.log(JSON.stringify({ start, end: Date.now() }))
		${ending}
	`
	return new Promise((done, fail) => {
		const child = spawn(process.execPath, ['--no-warnings', '--input-type=module', '-e', script], { stdio: ['ignore', 'pipe', 'pipe'] })
		let out = ''
		let err = ''
		child.stdout.on('data', (chunk: Buffer) => (out += chunk))
		child.stderr.on('data', (chunk: Buffer) => (err += chunk))
		child.on('error', fail)
		child.on('exit', (code) => {
			try {
				done({ ...(JSON.parse(out) as Run), code })
			} catch {
				fail(new Error(`run printed ${JSON.stringify(out)}, exit ${code}, stderr: ${err}`))
			}
		})
	})
}

test('two runs started together run one after the other', async () => {
	const [a, b] = await Promise.all([run(600, 'release()'), run(600, 'release()')])
	const [first, second] = a.start < b.start ? [a, b] : [b, a]
	expect(second.start).toBeGreaterThanOrEqual(first.end)
	expect(existsSync(lock)).toBe(false)
}, 20_000)

test('a run that crashes with an uncaught error frees the lock for the next one', async () => {
	const crashed = await run(100, "throw new Error('boom')")
	expect(crashed.code).not.toBe(0)
	expect(existsSync(lock)).toBe(false)
	const release = await takeRunLock(lock, { pollMs: 50 })
	release()
}, 20_000)

test('a lock older than the stale age is taken over; a fresh one is waited for', async () => {
	mkdirSync(lock)
	const old = new Date(Date.now() - 41 * 60_000)
	utimesSync(lock, old, old)
	const release = await takeRunLock(lock, { pollMs: 50 })
	release()

	mkdirSync(lock)
	let waited = false
	const pending = takeRunLock(lock, { pollMs: 50, onWait: () => (waited = true) })
	await new Promise((r) => setTimeout(r, 300))
	expect(waited).toBe(true)
	rmSync(lock, { recursive: true })
	;(await pending)()
	expect(existsSync(lock)).toBe(false)
}, 20_000)

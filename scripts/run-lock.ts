import { mkdirSync, rmdirSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

// One e2e run at a time on this PC, across worktrees and sessions: overlapping runs slow each other 2-4x and fail on
// launch timeouts and the WebView2 profile-lock stall. The lock is an empty folder made with mkdir, the same one the
// sessions' pw.sh wrapper takes, so the two never run together. It must stay empty: pw.sh removes it with rmdir.
export const RUN_LOCK_DIR = join(tmpdir(), 'zing-pw.lock')

// A run's main process that crashed can't remove its lock, so a lock this old belongs to a dead run (as in pw.sh).
export const STALE_AFTER_MS = 40 * 60_000

export type RunLockOptions = { pollMs?: number; staleAfterMs?: number; onWait?: () => void }

function tryTake(dir: string): boolean {
	try {
		mkdirSync(dir)
		return true
	} catch (err) {
		if ((err as NodeJS.ErrnoException).code === 'EEXIST') return false
		throw err
	}
}

function removeIfStale(dir: string, staleAfterMs: number): void {
	let ageMs: number
	try {
		ageMs = Date.now() - statSync(dir).mtimeMs
	} catch (err) {
		if ((err as NodeJS.ErrnoException).code === 'ENOENT') return
		throw err
	}
	if (ageMs <= staleAfterMs) return
	try {
		rmdirSync(dir)
	} catch (err) {
		if ((err as NodeJS.ErrnoException).code !== 'ENOENT') throw err
	}
}

// Waits for the lock, however long, and returns the function that releases it. It is also released when the process
// exits, including on an uncaught error or Ctrl+C.
export async function takeRunLock(dir: string, options: RunLockOptions = {}): Promise<() => void> {
	const { pollMs = 5_000, staleAfterMs = STALE_AFTER_MS, onWait } = options
	let waited = false
	while (!tryTake(dir)) {
		removeIfStale(dir, staleAfterMs)
		if (tryTake(dir)) break
		if (!waited) onWait?.()
		waited = true
		await new Promise((resolve) => setTimeout(resolve, pollMs))
	}
	let held = true
	const release = (): void => {
		if (!held) return
		held = false
		process.off('exit', release)
		rmdirSync(dir)
	}
	process.on('exit', release)
	return release
}

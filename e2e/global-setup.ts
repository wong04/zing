import { RUN_LOCK_DIR, takeRunLock } from '../scripts/run-lock'

// Playwright runs this once, in its main process, before any test. CI runners have the machine to themselves. A wrapper
// that already holds the lock (pw.sh) sets ZING_PW_LOCK_HELD=1, or this would wait for itself.
export default async function globalSetup(): Promise<(() => void) | undefined> {
	if (process.env.CI || process.env.ZING_PW_LOCK_HELD === '1') return undefined
	const release = await takeRunLock(RUN_LOCK_DIR, {
		onWait: () => console.log(`Waiting for another e2e run on this PC to finish (lock: ${RUN_LOCK_DIR})`),
	})
	// Playwright calls the returned function as the global teardown, after the last test and on Ctrl+C.
	return release
}

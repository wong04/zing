import { expect } from '@playwright/test'
import { HostProcessError, launchZing, test } from './zing'

// The harness itself: a run that went wrong must fail its test, even when the host recorded nothing.

test('a host that ends with a non-zero code during quit fails close(), though it recorded no error', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate(() => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'exit-code-on-quit', code: 3 }))
	await expect(zing.close()).rejects.toThrow(/Zing quit with exit code 3 \(0x3\)/)
})

test('a test can not override the variables the harness sets', async () => {
	await expect(launchZing({ env: { ZING_USER_DATA: 'C:\\elsewhere' } })).rejects.toThrow(HostProcessError)
	await expect(launchZing({ env: { ZING_E2E: '0' } })).rejects.toThrow(/ZING_E2E/)
})

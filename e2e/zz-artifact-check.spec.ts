import { expect } from '@playwright/test'
import { launchZing, test } from './zing'

// TEMPORARY: fails on purpose to check that a failed test's pictures and logs reach the CI artifact.
test('artifact check (fails on purpose)', async () => {
	const zing = await launchZing()
	await expect(zing.chrome.locator('.this-is-not-there')).toHaveCount(1, { timeout: 2_000 })
	await zing.close()
})

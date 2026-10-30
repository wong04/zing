import { expect, test } from '@playwright/test'
import { launchZing, MainProcessError } from './zing'

// The harness itself: an uncaught main-process error must fail the test that caused it.
test('an uncaught main-process error fails the test at close', async () => {
	const { app } = await launchZing()
	await app.evaluate(() => {
		setTimeout(() => {
			throw new Error('deliberate guard check')
		})
	})
	await expect.poll(() => app.evaluate(() => true)).toBe(true)
	await new Promise((resolve) => setTimeout(resolve, 200))
	await expect(app.close()).rejects.toThrow(MainProcessError)
})

test('an unhandled promise rejection in main fails the test too', async () => {
	const { app } = await launchZing()
	await app.evaluate(() => {
		void Promise.reject(new Error('deliberate rejection check'))
	})
	await new Promise((resolve) => setTimeout(resolve, 200))
	await expect(app.close()).rejects.toThrow(/deliberate rejection check/)
})

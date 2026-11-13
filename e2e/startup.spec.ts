// Start-up's critical path: the WebView2 engine is what takes longest to start, so it is asked for as soon as the one
// setting it needs is read, and the rest of start-up runs while its browser process starts (audit finding 2).
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, startupMarks, test } from './zing'

declare const window: { readonly zing: ZingApi }

test('a relaunch asks for the engine straight after reading settings, and opens the other services while it starts', async () => {
	const first = await launchZing()
	await first.chrome.evaluate(() => window.zing.setSetting('searchEngine', 'duckduckgo'))
	await first.close()
	const zing = await launchZing({ userDataDir: first.userDataDir })
	const { processStart, marks } = await startupMarks(zing.chrome)
	// "engine ready" is left out of the order: the engine may answer at once, inside the request.
	const host = marks.map((mark) => mark.name).filter((name) => name !== 'engine ready')
	expect(host).toEqual(['main', 'profile claimed', 'runtime found', 'dispatcher', 'settings read', 'engine requested', 'services open', 'window created'])
	for (const mark of marks) expect(mark.at).toBeGreaterThanOrEqual(processStart)
	const chromePainted = await zing.chrome.evaluate(async () => {
		for (;;) {
			const paint = performance.getEntriesByName('first-contentful-paint')[0]
			if (paint) return performance.timeOrigin + paint.startTime
			await new Promise((resolve) => setTimeout(resolve, 20))
		}
	})
	expect(chromePainted).toBeGreaterThan(marks.find((mark) => mark.name === 'engine ready')!.at)
	// The services were opened on the profile, settings included.
	expect((await zing.chrome.evaluate(() => window.zing.getSettings())).searchEngine).toBe('duckduckgo')
	await zing.close()
})

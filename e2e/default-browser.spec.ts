// Phase 8 task 7: the offer to make Zing the default browser, and Settings' Default browser row. An e2e run is never installed, so the
// offer shows only through "show-default-browser-offer", which skips the installed/default checks but keeps the person's own:
// never on a profile's first launch, never once they said don't ask again. Make default is recorded (ZING_DIALOGS_FILE), never launched.
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, layers, pageWhere, pages, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const OFFER_TEXT = 'Make Zing your default browser?'
const SETTINGS_URI = 'ms-settings:defaultapps?registeredAppUser=Zing'

const post = (chrome: Page, message: Record<string, unknown>): Promise<void> =>
	chrome.evaluate((m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage(m), message)

const isOverlay = (page: Page): boolean => page.url().endsWith('#overlay')

/** Whether the window shows the notice layer: a hidden view keeps its page. */
const noticeShown = async (chrome: Page): Promise<boolean> => (await layers(chrome)).some((layer) => layer.url.endsWith('#overlay') && layer.visible)

/** Startup has been through ZingApp.Ready, which decides on the offer and then marks the profile as launched before. */
async function launched(userDataDir?: string): Promise<Launched> {
	const zing = await launchZing(userDataDir === undefined ? {} : { userDataDir })
	await expect.poll(() => zing.chrome.evaluate(async () => (await window.zing.getSettings()).launchedBefore)).toBe(true)
	return zing
}

/** Asks for the offer, then waits long enough that one would be on screen: nothing to poll for when nothing should come. */
async function askForNothing(zing: Launched): Promise<void> {
	await post(zing.chrome, { kind: 'test', action: 'show-default-browser-offer' })
	await zing.chrome.waitForTimeout(2000)
	expect(await noticeShown(zing.chrome)).toBe(false)
	for (const overlay of pages(zing.browser).filter(isOverlay)) await expect(overlay.getByTestId('default-browser-offer')).toHaveCount(0)
}

async function showOffer(zing: Launched): Promise<Page> {
	await post(zing.chrome, { kind: 'test', action: 'show-default-browser-offer' })
	await expect.poll(() => noticeShown(zing.chrome)).toBe(true)
	const overlay = await pageWhere(zing.browser, isOverlay)
	await expect(overlay.getByTestId('default-browser-offer')).toContainText(OFFER_TEXT)
	return overlay
}

test('the default-browser offer waits for a second launch, opens Default apps, and stops after Don’t ask again', async () => {
	test.setTimeout(240_000)
	// A fresh profile's first launch: nothing at startup, nor when asked.
	const first = await launched()
	expect(await noticeShown(first.chrome)).toBe(false)
	await askForNothing(first)
	await first.close()

	// Its second launch: still nothing at startup (not installed), and the offer when asked.
	const second = await launched(first.userDataDir)
	expect(await noticeShown(second.chrome)).toBe(false)
	let overlay = await showOffer(second)
	await overlay.getByRole('button', { name: 'Make default' }).click()
	await expect.poll(() => second.dialogs()).toEqual([{ kind: 'default-apps', url: SETTINGS_URI }])
	await expect.poll(() => noticeShown(second.chrome)).toBe(false)
	// Make default is not a dismissal.
	expect((await second.chrome.evaluate(() => window.zing.getSettings())).defaultBrowserDismissed).toBe(false)

	overlay = await showOffer(second)
	await overlay.getByRole('button', { name: "Don't ask again" }).click()
	await expect.poll(() => second.chrome.evaluate(async () => (await window.zing.getSettings()).defaultBrowserDismissed)).toBe(true)
	await expect.poll(() => noticeShown(second.chrome)).toBe(false)
	await askForNothing(second)
	await second.close()

	// The same profile again: dismissed for good.
	const third = await launched(first.userDataDir)
	await askForNothing(third)
	expect(third.dialogs()).toEqual([])
	await third.close()
})

test('Settings shows the default-browser row by install state', async () => {
	test.setTimeout(120_000)
	const notInstalled = await launchZing()
	await notInstalled.chrome.evaluate(() => window.zing.openSettings('general'))
	let sheet = await pageWhere(notInstalled.browser, (p) => p.url().endsWith('#surface'))
	await expect(sheet.getByText('Install Zing to set it as default')).toBeVisible()
	await expect(sheet.getByRole('button', { name: 'Make default' })).toHaveCount(0)
	await notInstalled.close()

	const isDefault = await launchZing({ env: { ZING_DEFAULT_BROWSER: 'ZingHTML' } })
	await isDefault.chrome.evaluate(() => window.zing.openSettings('general'))
	sheet = await pageWhere(isDefault.browser, (p) => p.url().endsWith('#surface'))
	await expect(sheet.getByText('Zing is your default browser')).toBeVisible()
	await isDefault.close()
})

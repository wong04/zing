// Phase 8 task 6: the "Relaunch to update" pill and About's update controls, over the e2e update source (FakeUpdateSource),
// which never contacts GitHub or restarts: "update-ready" makes the next check find a version, "update-restarts" counts restarts.
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, layers, pages, pageWhere, scratchDir, test } from './zing'

declare const window: { readonly zing: ZingApi }

const post = (chrome: Page, message: Record<string, unknown>): Promise<void> =>
	chrome.evaluate((m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage(m), message)

const isChrome = (page: Page): boolean => page.url().endsWith('#chrome')
const isPill = (page: Page): boolean => page.url().endsWith('#update-pill')

/** Whether this window really shows the pill's view (TestHooks "layers"); a view the host has hidden still has its page. */
const pillShown = async (chrome: Page): Promise<boolean> => (await layers(chrome)).some((layer) => layer.url.endsWith('#update-pill') && layer.visible)

/** The pill view sits at the content's bottom right: its edges 10 px in, which with the view's 6 px padding leaves a 16 px gap. */
async function expectInCorner(chrome: Page): Promise<void> {
	const shown = await layers(chrome)
	const content = shown.find((layer) => layer.url.endsWith('#chrome'))!
	const pill = shown.find((layer) => layer.url.endsWith('#update-pill'))!
	expect(pill.width).toBeGreaterThan(0)
	expect(pill.height).toBeGreaterThan(0)
	expect(content.x + content.width - (pill.x + pill.width)).toBeCloseTo(10, 0)
	expect(content.y + content.height - (pill.y + pill.height)).toBeCloseTo(10, 0)
}

async function restarts(chrome: Page): Promise<number> {
	const file = join(scratchDir('zing-update-'), 'restarts.json')
	await post(chrome, { kind: 'test', action: 'update-restarts', file })
	await expect.poll(() => existsSync(file)).toBe(true)
	return (JSON.parse(readFileSync(file, 'utf8')) as { restarts: number }).restarts
}

test('Check now finds a ready update, every window shows the pill, and clicking it restarts once', async () => {
	test.setTimeout(120_000)
	const zing = await launchZing()
	expect(await pillShown(zing.chrome)).toBe(false)
	expect(pages(zing.browser).some(isPill)).toBe(false)

	await post(zing.chrome, { kind: 'test', action: 'update-ready', version: '0.9.1' })
	await zing.chrome.evaluate(() => window.zing.openSettings('about'))
	const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(sheet.getByRole('switch', { name: 'Check for updates' })).toHaveAttribute('aria-checked', 'true')
	await expect(sheet.getByText('Asks GitHub for the latest version every few hours. Sends nothing about you.')).toBeVisible()
	await expect(sheet.getByText('Version 0.9.0')).toBeVisible()
	await sheet.getByRole('button', { name: 'Check now' }).click()
	await expect(sheet.getByText('Ready: relaunch to update')).toBeVisible()

	await expect.poll(() => pillShown(zing.chrome)).toBe(true)
	const first = await pageWhere(zing.browser, isPill)
	await expectInCorner(zing.chrome)
	await expect(first.getByRole('button', { name: 'Relaunch to update' })).toBeVisible()

	await zing.chrome.evaluate(() => window.zing.runCommand('window.new'))
	await expect.poll(() => pages(zing.browser).filter(isChrome).length).toBe(2)
	const second = pages(zing.browser).find((p) => isChrome(p) && p !== zing.chrome)!
	await expect.poll(() => pillShown(second)).toBe(true)
	await expectInCorner(second)
	await expect.poll(() => pages(zing.browser).filter(isPill).length).toBe(2)
	for (const pill of pages(zing.browser).filter(isPill)) await expect(pill.getByRole('button', { name: 'Relaunch to update' })).toBeVisible()

	expect(await restarts(zing.chrome)).toBe(0)
	await first.getByRole('button', { name: 'Relaunch to update' }).click()
	await expect.poll(() => restarts(zing.chrome)).toBe(1)
	await first.getByRole('button', { name: 'Relaunch to update' }).click()
	// Answered after the click's call on the same view: the second relaunch has been handled.
	await first.evaluate(() => window.zing.getUpdateState())
	expect(await restarts(zing.chrome)).toBe(1)
	await zing.close()
})

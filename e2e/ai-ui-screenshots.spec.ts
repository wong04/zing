// AI control's share UI, light and dark, into docs/screenshots/ai-control: the strip with the shared tab's outline, the confirm
// bar, and Settings' section. Skipped unless ZING_SCREENSHOTS=1, as it is run by hand.
import { mkdirSync, readFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { connectMcp, textOf, type McpClient } from './mcp'
import { viewAccent } from './theme-helpers'
import { launchZing, pageWhere, test } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots', 'ai-control')
const MODES = ['light', 'dark'] as const
const FORM = readFileSync(join(import.meta.dirname, 'fixtures', 'ai-act', 'react-form.html'))

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((_req, res) => {
		res.setHeader('content-type', 'text/html')
		res.end(FORM)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

async function themed(view: Page, mode: (typeof MODES)[number]): Promise<void> {
	// Playwright pins every page it attaches to at light, over the host's override.
	await view.emulateMedia({ colorScheme: mode })
	await expect.poll(async () => (await viewAccent(view)).dark).toBe(mode === 'dark')
}

async function sendRef(mcp: McpClient, tab: string): Promise<string> {
	let ref: string | undefined
	await expect.poll(async () => (ref = /button "Send" \[(e\d+)\]/.exec(textOf(await mcp.call('snapshot', { tab })))?.[1])).toBeDefined()
	return ref!
}

test.describe('AI control screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the AI control pictures')

	for (const mode of MODES) {
		test(`the strip, the confirm bar and Settings, ${mode}`, async () => {
			test.setTimeout(150_000)
			mkdirSync(OUT_DIR, { recursive: true })
			const zing = await launchZing()
			await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await themed(zing.chrome, mode)
			const mcp = await connectMcp(zing.chrome)
			await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))
			const id = (await zing.chrome.evaluate(() => window.zing.getTabs())).activeId!
			await zing.chrome.evaluate(([tab, url]) => window.zing.navigateTab(tab!, url!), [id, `${base}/form`])
			await zing.chrome.evaluate((tab) => window.zing.openTabMenu(tab, { x: 40, y: 40 }), id)
			await expect.poll(() => zing.menus().at(-1)).toContain('Let AI use this tab')
			await zing.chrome.evaluate(() =>
				(globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'menu-choose', label: 'Let AI use this tab' }),
			)
			let tab = ''
			await expect.poll(async () => (tab = (JSON.parse(textOf(await mcp.call('list_tabs'))) as { tab: string }[])[0]?.tab ?? '')).not.toBe('')

			const strip = await pageWhere(zing.browser, (p) => p.url().endsWith('#ai-strip'))
			await themed(strip, mode)
			await expect(strip.getByText('AI is using this tab')).toBeVisible()
			await strip.waitForTimeout(400)
			await strip.locator('.ai-bar-frame').screenshot({ path: join(OUT_DIR, `ai-strip-${mode}.png`), omitBackground: true })
			await zing.chrome.locator(`[data-tab-id="${id}"]`).first().screenshot({ path: join(OUT_DIR, `ai-tab-outline-${mode}.png`) })

			const click = mcp.call('click', { tab, ref: await sendRef(mcp, tab) })
			const confirm = await pageWhere(zing.browser, (p) => p.url().endsWith('#ai-confirm'))
			await themed(confirm, mode)
			await expect(confirm.getByText('AI wants to', { exact: false })).toBeVisible({ timeout: 15_000 })
			await confirm.waitForTimeout(400)
			await confirm.locator('.ai-bar-frame').screenshot({ path: join(OUT_DIR, `ai-confirm-${mode}.png`), omitBackground: true })
			// Always allow puts the site in Settings' list.
			await confirm.getByRole('button', { name: 'Always allow on this site' }).click()
			await click

			await zing.chrome.evaluate(() => window.zing.openSettings('advanced'))
			const sheet = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
			await themed(sheet, mode)
			await sheet.getByText('Connect Claude Code').scrollIntoViewIfNeeded()
			await expect(sheet.getByText('Connect Claude Code')).toBeVisible()
			await sheet.waitForTimeout(400)
			await sheet.getByRole('dialog', { name: 'Settings' }).screenshot({ path: join(OUT_DIR, `ai-settings-${mode}.png`) })

			// The privacy banner warns while AI control is on: its token is kept on disk.
			await sheet.getByRole('button', { name: 'Privacy', exact: true }).click()
			await expect(sheet.getByText(/access token for AI control/)).toBeVisible()
			await sheet.waitForTimeout(400)
			await sheet.getByRole('dialog', { name: 'Settings' }).screenshot({ path: join(OUT_DIR, `ai-privacy-banner-${mode}.png`) })
			await zing.close()
		})
	}
})

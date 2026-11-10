// Light and dark pictures of UI that a parity row needs to close (B14, B18, B29, B31), into docs/screenshots.
// Each is the view's own DevTools picture, so the window stays off screen.
// Skipped unless ZING_SCREENSHOTS=1, as it is run by hand.
import { mkdirSync, writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { viewAccent } from './theme-helpers'
import { launchZing, pageWhere, scratchDir, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots')
const MODES = ['light', 'dark'] as const
type Mode = (typeof MODES)[number]

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const title = decodeURIComponent((req.url ?? '/').slice(1)) || 'Home'
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>${title}</title><body style="font:16px system-ui;margin:0;padding:48px;background:#fff;color:#222">
<h1>${title}</h1><p>A needle in the hay, another needle, and a third needle.</p><p>${'Lorem ipsum dolor sit amet. '.repeat(30)}</p></body>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

/** Sets Zing's theme and puts this view in the same appearance: Playwright pins every page it attaches to at light. */
async function appearance(zing: Launched, view: Page, mode: Mode): Promise<void> {
	await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
	await view.emulateMedia({ colorScheme: mode })
	await expect.poll(async () => (await viewAccent(view)).dark).toBe(mode === 'dark')
}

async function openPage(chrome: Page, title: string): Promise<void> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}/${title}`])
	await expect.poll(async () => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.title).toBe(title)
}

const shoot = async (view: Page, name: string, mode: Mode): Promise<void> => {
	await view.waitForTimeout(400)
	await view.screenshot({ path: join(OUT_DIR, `${name}-${mode}.png`) })
}

test.describe('Parity screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the parity pictures')
	test.beforeAll(() => mkdirSync(OUT_DIR, { recursive: true }))

	for (const mode of MODES) {
		test(`B14 the find bar, ${mode}`, async () => {
			const zing = await launchZing({ env: { ZING_START_URL: `${base}/Find` } })
			await pageWhere(zing.browser, (p) => p.url() === `${base}/Find`)
			await zing.chrome.evaluate(() => window.zing.runCommand('page.find'))
			const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
			await appearance(zing, ui, mode)
			await ui.getByRole('textbox', { name: 'Find in page' }).fill('needle')
			await expect(ui.getByText('1 of 3')).toBeVisible()
			await shoot(ui, 'b14-find-bar', mode)
			await zing.close()
		})

		test(`B18 the tab overview, ${mode}`, async () => {
			const zing = await launchZing()
			for (const title of ['Inbox', 'Docs', 'Calendar']) await openPage(zing.chrome, title)
			await zing.chrome.evaluate(() => window.zing.runCommand('tabs.overview'))
			const overview = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay-overview'))
			await appearance(zing, overview, mode)
			await expect(overview.locator('.overview-card')).toHaveCount(4)
			await expect(overview.locator('.overview-thumb img')).not.toHaveCount(0)
			await shoot(overview, 'b18-tab-overview', mode)
			await zing.close()
		})

		test(`B29 a tool panel minimised to the sidebar, ${mode}`, async () => {
			// The app sidebar, with its Minimized list, sits beside the page when the tabs are on top.
			const profile = scratchDir('zing-host-data-')
			writeFileSync(join(profile, 'settings.json'), JSON.stringify({ tabBarPosition: 'top' }))
			const zing = await launchZing({ userDataDir: profile })
			const { chrome } = zing
			await appearance(zing, chrome, mode)
			await chrome.evaluate(() => window.zing.openPanel('tool:Hashes', { x: 600, y: 300 }))
			const { panels } = await chrome.evaluate(() => window.zing.getToolPanels())
			await chrome.evaluate((id) => window.zing.minimizePanel(id), panels[0]!.id)
			const row = chrome.getByTestId('minimized-row').filter({ hasText: 'Hashes' })
			await expect(row).toBeVisible()
			await chrome.waitForTimeout(400)
			const sidebar = await chrome.getByRole('navigation', { name: 'App sidebar' }).boundingBox()
			if (!sidebar) throw new Error('the sidebar has no box')
			await chrome.screenshot({ path: join(OUT_DIR, `b29-minimized-panel-${mode}.png`), clip: sidebar })
			await zing.close()
		})

		test(`B31 the account gate, ${mode}`, async () => {
			const zing = await launchZing({ env: { ZING_SKIP_ACCOUNT_GATE: '' } })
			const gate = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
			await appearance(zing, gate, mode)
			await expect(gate.getByRole('heading', { name: 'Create your account' })).toBeVisible()
			await shoot(gate, 'b31-account-gate', mode)
			// Zing doesn't quit while the gate is up.
			await gate.getByRole('button', { name: 'Skip for now' }).click()
			await zing.close()
		})
	}
})

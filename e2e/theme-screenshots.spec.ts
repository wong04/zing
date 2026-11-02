// Phase 5 PR A: one picture of the chrome for each preset, tab position and appearance, into docs/screenshots/phase-5.
// Skipped unless ZING_SCREENSHOTS=1, as it takes 48 pictures and is run by hand.
import { mkdirSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { accentThemes } from '../src/shared/theme/accent'
import { expectAccent, viewAccent } from './theme-helpers'
import { launchZing, test } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots', 'phase-5')
const POSITIONS = ['top', 'left', 'right'] as const
const MODES = ['light', 'dark'] as const

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const title = decodeURIComponent((req.url ?? '/').slice(1)) || 'Home'
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>${title}</title><body>${title}</body>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const tabsOf = (chrome: Page) => chrome.evaluate(() => window.zing.getTabs())

async function openPage(chrome: Page, title: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await tabsOf(chrome)
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}/${title}`])
	await expect.poll(async () => (await tabsOf(chrome)).tabs.find((t) => t.id === activeId)?.title).toBe(title)
	return activeId!
}

async function addFavourite(chrome: Page, title: string): Promise<void> {
	await openPage(chrome, title)
	await chrome.evaluate(() => window.zing.runCommand('library.addFavourite'))
}

test.describe('Phase 5 screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the Phase 5 pictures')

	for (const position of POSITIONS) {
		test(`every preset, light and dark, with the tabs on the ${position}`, async () => {
			test.setTimeout(600_000)
			mkdirSync(OUT_DIR, { recursive: true })
			const zing = await launchZing()
			const chrome = zing.chrome
			for (let i = 1; i <= 16; i++) await addFavourite(chrome, `Fav${i}`)
			const left = await openPage(chrome, 'Alpha')
			const right = await openPage(chrome, 'Beta')
			await chrome.evaluate(([a, b]) => window.zing.splitWith(a!, b!), [left, right])
			await chrome.evaluate((side) => window.zing.setSetting('tabBarPosition', side), position)

			for (const mode of MODES) {
				await chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
				await expect.poll(async () => (await viewAccent(chrome)).dark).toBe(mode === 'dark')
				for (const preset of accentThemes) {
					await chrome.evaluate((accent) => window.zing.setSetting('accent', accent), preset)
					await expectAccent(chrome, preset)
					await chrome.screenshot({ path: join(OUT_DIR, `${preset}-${position}-${mode}.png`) })
				}
			}
			await zing.close()
		})
	}
})

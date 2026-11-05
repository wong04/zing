import { existsSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { startEngineFixture, type EngineFixture } from './engineFixture'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

let fixture: EngineFixture

test.afterEach(async () => {
	await fixture?.close()
})

/** Settings > Files > Engines > Manage, which is how the panel is reached (the Toolbox has no tile for it). */
async function openEnginesPanel(zing: Launched): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.evaluate(() => window.zing.openSettings('files'))
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await surface.getByRole('button', { name: 'Manage engines' }).click()
	const panel = surface.getByRole('dialog', { name: 'Engines' })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

const row = (panel: Locator, name: string): Locator => panel.getByRole('listitem').filter({ hasText: name })

test('opening Zing downloads nothing', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const engines = await zing.chrome.evaluate(() => window.zing.listEngines())
	expect(engines.map((e) => e.id).sort()).toEqual(['bgmodel', 'ffmpeg', 'magick', 'pandoc', 'ytdlp'])
	expect(engines.every((e) => e.installedVersion === undefined)).toBe(true)
	// Opening the panel lists what is on disk; it makes no request either.
	await openEnginesPanel(zing)
	expect(fixture.requests).toEqual([])
	expect(existsSync(join(zing.userDataDir, 'engines'))).toBe(false)
	await zing.close()
})

test('the Engines panel lists sizes and Remove frees one', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const { panel } = await openEnginesPanel(zing)
	const ffmpeg = row(panel, 'FFmpeg')
	await expect(ffmpeg).toContainText('Not downloaded')
	// The person is told the size and the source before anything is fetched.
	await expect(ffmpeg).toContainText('KB download')
	await expect(ffmpeg).toContainText(`/ffmpeg.exe`)
	expect(fixture.requests).toEqual([])

	await ffmpeg.getByRole('button', { name: 'Download FFmpeg' }).click()
	await expect(ffmpeg).toContainText('Downloaded')
	expect(existsSync(join(zing.userDataDir, 'engines', 'ffmpeg', 'ffmpeg.exe'))).toBe(true)
	expect(fixture.requests).toEqual(['/ffmpeg.exe'])

	await ffmpeg.getByRole('button', { name: 'Remove FFmpeg' }).click()
	await expect(ffmpeg).toContainText('Not downloaded')
	expect(existsSync(join(zing.userDataDir, 'engines', 'ffmpeg'))).toBe(false)
	await zing.close()
})

test('Cancel stops a download, leaves nothing behind and shows no error', async () => {
	fixture = await startEngineFixture({ slow: ['ffmpeg'] })
	const zing = await launchZing({ env: fixture.env })
	const { panel } = await openEnginesPanel(zing)
	const ffmpeg = row(panel, 'FFmpeg')
	await ffmpeg.getByRole('button', { name: 'Download FFmpeg' }).click()
	await expect(ffmpeg).toContainText('Downloading')
	await ffmpeg.getByRole('button', { name: 'Cancel FFmpeg' }).click()
	await expect(ffmpeg).toContainText('Not downloaded')
	await expect(ffmpeg.getByRole('button', { name: 'Download FFmpeg' })).toBeEnabled()
	await expect(panel.getByRole('alert')).toHaveCount(0)
	// The temp folder goes before Cancel is answered: nothing is left in the engines folder.
	const folder = join(zing.userDataDir, 'engines')
	expect(existsSync(folder) ? readdirSync(folder) : []).toEqual([])
	await zing.close()
})

test('a download that fails names the engine and the address to fetch it by hand, and leaves nothing', async () => {
	fixture = await startEngineFixture({ missing: ['pandoc'] })
	const zing = await launchZing({ env: fixture.env })
	const { panel } = await openEnginesPanel(zing)
	await row(panel, 'Pandoc').getByRole('button', { name: 'Download Pandoc' }).click()
	const alert = panel.getByRole('alert')
	await expect(alert).toContainText('Pandoc')
	await expect(alert).toContainText('by hand')
	await expect(alert).toContainText('/pandoc.exe')
	await expect(row(panel, 'Pandoc')).toContainText('Not downloaded')
	expect(existsSync(join(zing.userDataDir, 'engines', 'pandoc'))).toBe(false)
	await zing.close()
})

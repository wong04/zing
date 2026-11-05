import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { startEngineFixture, type EngineFixture } from './engineFixture'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

// The fake engine stands in for yt-dlp whenever it is given "--no-playlist", "-o <path>" and an address after "--"; the
// address picks what it does (see host/Zing.FakeEngine/Program.cs). Nothing here reaches a real site.
const VIDEO = 'https://video.example.com/watch?v=abc'
const SLOW = 'https://video.example.com/watch?v=slow'
const FORBIDDEN = 'https://video.example.com/watch?v=forbidden'
const BOT_CHECK = 'https://video.example.com/watch?v=bot-check'

let fixture: EngineFixture
let pageServer: Server
let base = ''

test.beforeAll(async () => {
	pageServer = createServer((req, res) => {
		res.setHeader('content-type', 'text/html; charset=utf-8')
		res.end('<!DOCTYPE html><html><head><title>Saved: fixture/page</title></head><body><p id="static">static marker</p><script>document.body.append(Object.assign(document.createElement("p"), { id: "dynamic", textContent: "dynamic marker" }))</script></body></html>')
	})
	await new Promise<void>((resolve) => pageServer.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(pageServer.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => pageServer.close(() => resolve())))

test.afterEach(async () => {
	await fixture?.close()
})

const downloadsOf = (zing: Launched): string => join(zing.userDataDir, 'downloads')
const filesIn = (dir: string): string[] => (existsSync(dir) ? readdirSync(dir).sort() : [])

/** Toolbox, then the Download tile: the panel opens centred, where all of it is on screen. */
async function openDownloadPanel(zing: Launched): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').filter({ hasText: 'Download' }).click()
	const panel = surface.getByRole('dialog', { name: 'Download', exact: true })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

async function installEngines(zing: Launched): Promise<void> {
	await zing.chrome.evaluate(async () => {
		await window.zing.ensureEngine('ytdlp')
		await window.zing.ensureEngine('ffmpeg')
	})
}

async function startDownload(panel: Locator, url: string): Promise<void> {
	await panel.getByLabel('Video link').fill(url)
	await panel.getByRole('button', { name: 'Download', exact: true }).click()
}

test('nothing is fetched until the button is pressed, and a download needs both engines first', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const { panel } = await openDownloadPanel(zing)

	await expect(panel.getByText('yt-dlp is needed')).toBeVisible()
	await expect(panel.getByText('FFmpeg is needed')).toBeVisible()
	await panel.getByLabel('Video link').fill(VIDEO)
	await expect(panel.getByRole('button', { name: 'Download', exact: true })).toBeDisabled()
	expect(fixture.requests).toEqual([])

	await panel.getByRole('button', { name: 'Download yt-dlp' }).click()
	await expect(panel.getByText('yt-dlp is needed')).toHaveCount(0)
	await panel.getByRole('button', { name: 'Download FFmpeg' }).click()
	await expect(panel.getByText('FFmpeg is needed')).toHaveCount(0)
	await expect(panel.getByRole('button', { name: 'Download', exact: true })).toBeEnabled()
	// The engines came down on the two clicks, and a download has not started.
	expect(fixture.requests.sort()).toEqual(['/ffmpeg.exe', '/yt-dlp.exe'])
	expect(filesIn(downloadsOf(zing))).toEqual([])
	await zing.close()
})

test('progress shows, the finished file is listed, and the next one is numbered', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await installEngines(zing)
	const { panel } = await openDownloadPanel(zing)

	await startDownload(panel, VIDEO)
	await expect(panel.getByText(/Downloading… \d+%|Finishing the file…/)).toBeVisible()
	await expect(panel.getByText('Saved audio.mp3 to your Downloads folder.')).toBeVisible()
	await expect(panel.getByRole('list', { name: 'Saved files' }).getByText('audio.mp3', { exact: true })).toBeVisible()
	expect(filesIn(downloadsOf(zing))).toEqual(['audio.mp3'])

	await panel.getByRole('radio', { name: 'MP4 (video)' }).click()
	await startDownload(panel, VIDEO)
	await expect(panel.getByText('Saved video.mp4 to your Downloads folder.')).toBeVisible()
	await startDownload(panel, VIDEO)
	await expect(panel.getByText('Saved video 2.mp4 to your Downloads folder.')).toBeVisible()
	expect(filesIn(downloadsOf(zing))).toEqual(['audio.mp3', 'video 2.mp4', 'video.mp4'])
	await expect(panel.getByRole('list', { name: 'Saved files' }).getByRole('listitem')).toHaveText(['audio.mp3', 'video.mp4', 'video 2.mp4'])
	await zing.close()
})

test('Cancel stops the job and leaves no file', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await installEngines(zing)
	const { panel } = await openDownloadPanel(zing)

	await startDownload(panel, SLOW)
	await expect(panel.getByText('Downloading… 10%')).toBeVisible()
	await panel.getByRole('button', { name: 'Cancel' }).click()

	await expect(panel.getByText('Cancelled. Nothing was kept.')).toBeVisible()
	expect(filesIn(downloadsOf(zing))).toEqual([])
	const jobs = join(zing.userDataDir, 'engine-jobs')
	expect(existsSync(jobs) ? readdirSync(jobs) : []).toEqual([])
	await zing.close()
})

test('closing the panel ends the job, and nothing about it is kept', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await installEngines(zing)
	const { panel } = await openDownloadPanel(zing)

	await startDownload(panel, SLOW)
	await expect(panel.getByText('Downloading… 10%')).toBeVisible()
	await panel.getByRole('button', { name: 'Close Download' }).click()
	await expect(panel).toHaveCount(0)
	await expect.poll(() => zing.chrome.evaluate(() => window.zing.getToolPanels().then((s) => s.panels.length))).toBe(0)

	// Reopened, the panel is blank: no job, no link, no list.
	const { panel: again } = await openDownloadPanel(zing)
	await expect(again.getByLabel('Video link')).toHaveValue('')
	await expect(again.getByRole('status')).toHaveCount(0)
	await expect(again.getByRole('list', { name: 'Saved files' })).toHaveCount(0)
	expect(filesIn(downloadsOf(zing))).toEqual([])
	await zing.close()
})

test('a 403 offers the Update button, and nothing updates until it is clicked', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await installEngines(zing)
	const { panel } = await openDownloadPanel(zing)
	const requestsBefore = fixture.requests.length

	await startDownload(panel, FORBIDDEN)

	await expect(panel.getByText(/HTTP 403/)).toBeVisible()
	const update = panel.getByRole('button', { name: 'Update yt-dlp' })
	await expect(update).toBeVisible()
	expect(fixture.requests.length).toBe(requestsBefore)
	expect(filesIn(downloadsOf(zing))).toEqual([])

	await update.click()
	await expect.poll(() => fixture.requests.slice(requestsBefore).some((r) => r.includes('SHA2-256SUMS'))).toBe(true)
	await zing.close()
})

test('a bot check is reported in plain words: no update, no way round it', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await installEngines(zing)
	const { panel } = await openDownloadPanel(zing)
	const requestsBefore = fixture.requests.length

	await startDownload(panel, BOT_CHECK)

	const alert = panel.getByRole('alert').filter({ hasText: 'not a bot' })
	await expect(alert).toBeVisible()
	await expect(alert).toContainText("Zing doesn't work around that")
	await expect(alert).not.toContainText('cookies')
	await expect(panel.getByRole('button', { name: 'Update yt-dlp' })).toHaveCount(0)
	expect(fixture.requests.length).toBe(requestsBefore)
	expect(filesIn(downloadsOf(zing))).toEqual([])
	await zing.close()
})

test('a link to this PC, a local network, or a non-web scheme is refused before any job starts', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await installEngines(zing)
	const { panel } = await openDownloadPanel(zing)

	for (const bad of ['http://127.0.0.1:8080/a', 'http://192.168.1.10/a', 'http://localhost/a', 'file:///C:/x.mp4', '--exec calc']) {
		await startDownload(panel, bad)
		await expect(panel.getByRole('alert')).toBeVisible()
		await expect(panel.getByRole('status')).toHaveCount(0)
	}
	expect(filesIn(downloadsOf(zing))).toEqual([])
	await zing.close()
})

test('a renderer cannot hand yt-dlp a folder, a cookie option or FFmpeg\'s location', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await installEngines(zing)
	const { panel } = await openDownloadPanel(zing)
	const panelId = await zing.chrome.evaluate(() => window.zing.getToolPanels().then((s) => s.panels[0]!.id))
	void panel

	const attempt = (args: readonly unknown[]): Promise<string> =>
		zing.chrome.evaluate(
			([id, list]) => window.zing.runEngine('ytdlp', id as string, { args: list as never, urls: ['https://video.example.com/a'] }).then(() => 'started', (e: Error) => e.name),
			[panelId, args] as const,
		)
	expect(await attempt(['--ffmpeg-location', 'C:\\evil'])).toBe('DisallowedEngineFlagException')
	expect(await attempt(['--cookies-from-browser', 'chrome'])).toBe('DisallowedEngineFlagException')
	expect(await attempt(['--impersonate', 'chrome'])).toBe('DisallowedEngineFlagException')
	expect(await attempt(['-o', 'C:\\Users\\x\\a.mp4'])).toBe('UnsafeEngineArgumentException')
	await zing.close()
})

test('Save this page writes the open page as it is now to Downloads, numbered, and lists it', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: { ...fixture.env, ZING_START_URL: `${base}/saved` } })
	await pageWhere(zing.browser, (p) => p.url() === `${base}/saved`)
	const { panel } = await openDownloadPanel(zing)

	await panel.getByRole('button', { name: 'Save this page' }).click()
	await expect(panel.getByRole('list', { name: 'Saved files' }).getByText('Saved fixturepage.html', { exact: true })).toBeVisible()
	await panel.getByRole('button', { name: 'Save this page' }).click()
	await expect(panel.getByRole('list', { name: 'Saved files' }).getByText('Saved fixturepage 2.html', { exact: true })).toBeVisible()

	expect(filesIn(downloadsOf(zing))).toEqual(['Saved fixturepage 2.html', 'Saved fixturepage.html'])
	const html = readFileSync(join(downloadsOf(zing), 'Saved fixturepage.html'), 'utf8')
	expect(html.startsWith('\uFEFF<!DOCTYPE html>')).toBe(true)
	expect(html).toContain('static marker')
	// What the page holds now, including what its script added.
	expect(html).toContain('dynamic marker')
	await zing.close()
})

test('page:save refuses a page that is not a web page, and a web page cannot ask for it', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: { ...fixture.env, ZING_START_URL: `${base}/saved` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/saved`)

	// A page view has no host channel behind it: its call gets no answer and nothing is written.
	const answered = await page.evaluate(
		() =>
			new Promise<boolean>((resolve) => {
				const webview = (globalThis as unknown as { chrome?: { webview?: { postMessage(m: unknown): void; addEventListener(t: 'message', l: () => void): void } } }).chrome?.webview
				webview?.addEventListener('message', () => resolve(true))
				webview?.postMessage({ kind: 'invoke', id: 1, channel: 'page:save', args: [] })
				setTimeout(() => resolve(false), 1500)
			}),
	)
	expect(answered).toBe(false)
	expect(filesIn(downloadsOf(zing))).toEqual([])

	const tab = await zing.chrome.evaluate(async () => (await window.zing.getTabs()).tabs[0]!.id)
	await zing.chrome.evaluate((id) => window.zing.navigateTab(id, 'about:blank#nothing'), tab)
	await pageWhere(zing.browser, (p) => p.url() === 'about:blank#nothing')
	const refused = await zing.chrome.evaluate(() => window.zing.savePage().then(() => 'saved', (e: Error) => e.name))
	expect(refused).toBe('PageNotSavableException')
	expect(filesIn(downloadsOf(zing))).toEqual([])
	await zing.close()
})

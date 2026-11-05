import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import type { EngineExit, JobArg } from '../src/shared/engines/engineJob'
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

// Running engines. Zing.FakeEngine stands in for FFmpeg, so no real engine runs and nothing leaves this PC.

interface JobLog {
	lines: string[]
	exits: EngineExit[]
}

/** Opens a floating tool panel (a job belongs to one) and returns its id. */
async function openPanelId(zing: Launched): Promise<string> {
	await zing.chrome.evaluate(() => window.zing.openPanel('tool:Hashes', { x: 800, y: 300 }))
	const state = await zing.chrome.evaluate(() => window.zing.getToolPanels())
	return state.panels[0]!.id
}

/** The host's answer to the next file picker, queued first because an e2e run shows no dialog. */
async function queuePick(zing: Launched, ...files: string[]): Promise<void> {
	await zing.chrome.evaluate((paths) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: paths }), files)
}

/** Starts collecting every job's output and exit in the page, so a test can poll them. */
async function recordJobs(zing: Launched): Promise<void> {
	await zing.chrome.evaluate(() => {
		const log: JobLog = { lines: [], exits: [] }
		const stash = globalThis as unknown as { __jobs?: JobLog }
		stash.__jobs = log
		window.zing.onEngineOutput((output) => log.lines.push(...output.lines.map((l) => `${l.stream}:${l.text}`)))
		window.zing.onEngineExit((exit) => log.exits.push(exit))
	})
}

const exitsOf = (zing: Launched): Promise<EngineExit[]> => zing.chrome.evaluate(() => (globalThis as unknown as { __jobs?: JobLog }).__jobs!.exits)
const linesOf = (zing: Launched): Promise<string[]> => zing.chrome.evaluate(() => (globalThis as unknown as { __jobs?: JobLog }).__jobs!.lines)

test('a picked file reaches the engine as an absolute path, its output is placed in Downloads and the exit comes once', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	const panel = await openPanelId(zing)
	await recordJobs(zing)
	const input = join(zing.userDataDir, '-rf.mov')
	writeFileSync(input, 'not really a movie')
	await queuePick(zing, input)

	const picked = await zing.chrome.evaluate(() => window.zing.pickFiles(['video']))
	expect(picked).toHaveLength(1)
	expect(picked[0]!.name).toBe('-rf.mov')
	// The renderer gets a name and a token, never the path.
	expect(JSON.stringify(picked)).not.toContain(zing.userDataDir.replaceAll('\\', '\\\\'))
	const token = picked[0]!.token
	await zing.chrome.evaluate(([id, file]) => window.zing.runEngine('ffmpeg', id!, { args: ['--fake-write', { output: 0 }, { input: file! }], outputs: ['clip.txt'] }), [panel, token])

	await expect.poll(async () => (await exitsOf(zing)).length).toBe(1)
	const [exit] = await exitsOf(zing)
	const placed = join(zing.userDataDir, 'downloads', 'clip.txt')
	// A name and a token, not the path.
	expect(exit).toMatchObject({ code: 0, cancelled: false, outputs: [{ name: 'clip.txt', token: expect.stringMatching(/^[0-9a-f]{32}$/) }] })
	expect(JSON.stringify(exit?.outputs)).not.toContain(zing.userDataDir.replaceAll('\\', '\\\\'))
	expect(readFileSync(placed, 'utf8')).toBe('fake engine output\n')
	// The argument echo is the engine's first line: the file arrives whole, as a path that can't be read as an option.
	const echoed = JSON.parse((await linesOf(zing))[0]!.slice('out:'.length)) as string[]
	expect(echoed).toEqual(['-nostdin', '--fake-write', expect.stringMatching(/clip\.txt$/), input])
	// Nothing is left in the job folder, and a second exit never follows.
	await new Promise((resolve) => setTimeout(resolve, 500))
	expect(await exitsOf(zing)).toHaveLength(1)
	const jobs = join(zing.userDataDir, 'engine-jobs')
	expect(existsSync(jobs) ? readdirSync(jobs) : []).toEqual([])
	await zing.close()
})

test('closing the panel kills the job and leaves no output; minimizing it does not', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	const panel = await openPanelId(zing)
	await recordJobs(zing)

	await zing.chrome.evaluate((id) => window.zing.runEngine('ffmpeg', id, { args: ['--fake-write', { output: 0 }, '--err', 'ready', '--sleep', '60000'], outputs: ['clip.txt'] }), panel)
	await expect.poll(async () => (await linesOf(zing)).includes('err:ready')).toBe(true)
	await zing.chrome.evaluate((id) => window.zing.minimizePanel(id), panel)
	await new Promise((resolve) => setTimeout(resolve, 500))
	expect(await exitsOf(zing)).toEqual([])

	await zing.chrome.evaluate((id) => window.zing.closePanel(id), panel)
	await expect.poll(async () => (await exitsOf(zing)).length).toBe(1)
	expect((await exitsOf(zing))[0]).toMatchObject({ cancelled: true, outputs: [] })
	const downloads = join(zing.userDataDir, 'downloads')
	expect(existsSync(downloads) ? readdirSync(downloads) : []).toEqual([])
	const jobs = join(zing.userDataDir, 'engine-jobs')
	expect(existsSync(jobs) ? readdirSync(jobs) : []).toEqual([])
	await zing.close()
})

test('a job that was never given a file, or that would run before the engine is downloaded, is refused', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const panel = await openPanelId(zing)
	const run = (args: readonly JobArg[]) => zing.chrome.evaluate(([id, a]) => window.zing.runEngine('ffmpeg', id as string, { args: a as JobArg[] }).then(() => 'started', (e: Error) => e.message), [panel, args] as const)

	// Running never downloads: the engine must have been fetched on purpose first.
	expect(await run(['--exit', '0'])).toContain("hasn't been downloaded")
	expect(fixture.requests).toEqual([])

	await zing.chrome.evaluate(() => window.zing.ensureEngine('ffmpeg'))
	expect(await run([{ input: 'C:\\Windows\\win.ini' }])).toContain("wasn't picked or dropped")
	expect(await run(['-i', 'C:\\Windows\\win.ini'])).toContain('takes a file the person picked')
	expect(await run(['-vf', 'movie=C:/x'])).toContain("isn't given the option")
	expect(await run([{ input: '' }])).toContain("wasn't picked or dropped")
	await zing.close()
})

test('a file dropped on a Zing view is handed to the host as a token; a page has no way to message the host', async () => {
	fixture = await startEngineFixture()
	const zing = await launchZing({ env: fixture.env })
	const dropped = join(zing.userDataDir, 'dropped photo.png')
	writeFileSync(dropped, 'png')
	await zing.chrome.evaluate(() => {
		document.addEventListener('dragover', (event) => event.preventDefault())
		document.addEventListener('drop', (event) => {
			event.preventDefault()
			;(globalThis as unknown as { __dropped: Promise<unknown> }).__dropped = window.zing.openDropped([...event.dataTransfer!.files])
		})
	})
	const cdp = await zing.chrome.context().newCDPSession(zing.chrome)
	const data = { items: [], files: [dropped], dragOperationsMask: 1 }
	await cdp.send('Input.dispatchDragEvent', { type: 'dragEnter', x: 300, y: 300, data })
	await cdp.send('Input.dispatchDragEvent', { type: 'dragOver', x: 300, y: 300, data })
	await cdp.send('Input.dispatchDragEvent', { type: 'drop', x: 300, y: 300, data })

	const result = await zing.chrome.evaluate(() => (globalThis as unknown as { __dropped: Promise<unknown> }).__dropped)
	expect(result).toEqual([{ token: expect.stringMatching(/^[0-9a-f]{32}$/), name: 'dropped photo.png' }])

	// A web page cannot post to the host (messages are switched off for page views), so it cannot offer a file or ask for anything.
	const tab = await zing.chrome.evaluate(async () => (await window.zing.getTabs()).tabs[0]!.id)
	await zing.chrome.evaluate((id) => window.zing.navigateTab(id, 'about:blank#page-probe'), tab)
	const page = await pageWhere(zing.browser, (p) => p.url() === 'about:blank#page-probe')
	// The page may still see chrome.webview, but nothing on the host listens to a page view: a call gets no answer.
	const answered = await page.evaluate(
		() =>
			new Promise<boolean>((resolve) => {
				const webview = (globalThis as unknown as { chrome?: { webview?: { postMessage(m: unknown): void; addEventListener(t: 'message', l: () => void): void } } }).chrome?.webview
				webview?.addEventListener('message', () => resolve(true))
				webview?.postMessage({ kind: 'invoke', id: 1, channel: 'tabs:get', args: [] })
				setTimeout(() => resolve(false), 1500)
			}),
	)
	expect(answered).toBe(false)

	// A file dropped on a page is the browser's own business (whatever the page does with it, e.g. opening it); nothing is
	// granted to an engine, because only Zing's views can hand the host a file. (A real Explorer drop on a page is for a
	// by-hand check: the synthesised one here did not move the blank page.)
	const pageCdp = await zing.chrome.context().newCDPSession(page)
	for (const type of ['dragEnter', 'dragOver', 'drop'] as const) await pageCdp.send('Input.dispatchDragEvent', { type, x: 100, y: 100, data })
	await new Promise((resolve) => setTimeout(resolve, 500))
	const collected = await zing.chrome.evaluate(() => window.zing.openDropped([]).then(() => 'granted', (e: Error) => e.name))
	expect(collected).toBe('NoDroppedFilesException')
	await zing.close()
})

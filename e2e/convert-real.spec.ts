// Convert with the REAL engines: FFmpeg, ImageMagick and Pandoc, downloaded from the pinned official addresses in
// EngineCatalog.cs into this run's scratch profile (outside the repo), on files this test makes. The equivalent of a
// *.network.test: it needs the internet and about 170 MB, so it is skipped unless ZING_REAL_ENGINES=1 and is never in CI.
//   ZING_REAL_ENGINES=1 bash <pw.sh> e2e/convert-real.spec.ts
// The HEIC fixture is made with pillow-heif through uv (any HEIC encoder would do); the MOV with the downloaded FFmpeg.
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, type Locator } from '@playwright/test'
import type { EngineExit } from '../src/shared/engines/engineJob'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

test.describe('Convert with real engines', () => {
	test.skip(process.env.ZING_REAL_ENGINES !== '1', 'set ZING_REAL_ENGINES=1 to download and run the real engines')
	test.setTimeout(900_000)

	const HEIC_SCRIPT = [
		'from PIL import Image',
		'import pillow_heif, sys',
		'pillow_heif.register_heif_opener()',
		'Image.new("RGB", (64, 48), (200, 30, 30)).save(sys.argv[1], quality=80)',
	].join('\n')

	const ffmpegPath = (zing: Launched): string => join(zing.userDataDir, 'engines', 'ffmpeg', 'ffmpeg-9.0.2-essentials_build', 'bin', 'ffmpeg.exe')

	async function launchWithEngines(userDataDir?: string, ids: readonly string[] = ['ffmpeg', 'magick', 'pandoc']): Promise<Launched> {
		const zing = await launchZing(userDataDir === undefined ? {} : { userDataDir })
		for (const id of ids) expect(await zing.chrome.evaluate((engine) => window.zing.ensureEngine(engine), id)).toBe('installed')
		return zing
	}

	async function openConvert(zing: Launched): Promise<Locator> {
		await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
		const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
		await surface.getByTestId('toolbox-tile').getByText('Convert', { exact: true }).click()
		const panel = surface.getByRole('dialog', { name: 'Convert', exact: true })
		await expect(panel).toBeVisible()
		return panel
	}

	async function queuePick(zing: Launched, ...files: string[]): Promise<void> {
		await zing.chrome.evaluate((paths) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: paths }), files)
	}

	async function convertInTool(zing: Launched, panel: Locator, file: string, target: string): Promise<void> {
		await queuePick(zing, file)
		await panel.getByRole('button', { name: /^Choose / }).click()
		await expect(panel.getByText(file.split('\\').at(-1)!, { exact: true })).toBeVisible()
		await panel.getByRole('button', { name: 'Convert to', exact: true }).click()
		await panel.getByRole('option', { name: target }).click()
		await panel.getByRole('button', { name: 'Convert', exact: true }).click()
		await expect(panel.getByText('Saved to your Downloads folder:')).toBeVisible({ timeout: 300_000 })
	}

	test('a real HEIC becomes a JPG and a real HEVC MOV becomes an MP4', async () => {
		const zing = await launchWithEngines()
		const fixtures = join(zing.userDataDir, 'fixtures')
		mkdirSync(fixtures, { recursive: true })
		const heic = join(fixtures, 'IMG_0001.HEIC')
		const made = spawnSync('uv', ['run', '--no-project', '--with', 'pillow-heif', '--with', 'pillow', 'python', '-I', '-c', HEIC_SCRIPT, heic], { encoding: 'utf8' })
		expect(made.status, made.stderr).toBe(0)
		const mov = join(fixtures, 'clip.mov')
		// 10-bit HEVC with AAC, like an iPhone clip.
		const filmed = spawnSync(ffmpegPath(zing), ['-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=duration=2:size=320x240:rate=15', '-f', 'lavfi', '-i', 'sine=duration=2', '-c:v', 'libx265', '-pix_fmt', 'yuv420p10le', '-tag:v', 'hvc1', '-c:a', 'aac', mov], { encoding: 'utf8' })
		expect(filmed.status, filmed.stderr).toBe(0)

		const panel = await openConvert(zing)
		await convertInTool(zing, panel, heic, 'JPG')
		const downloads = join(zing.userDataDir, 'downloads')
		const jpg = readFileSync(join(downloads, 'IMG_0001.jpg'))
		expect([...jpg.subarray(0, 3)]).toEqual([0xff, 0xd8, 0xff])

		await convertInTool(zing, panel, mov, 'MP4')
		const mp4 = join(downloads, 'clip.mp4')
		expect(statSync(mp4).size).toBeGreaterThan(1000)
		const probe = spawnSync(ffmpegPath(zing), ['-hide_banner', '-i', mp4], { encoding: 'utf8' })
		expect(probe.stderr).toMatch(/Video: h264 .*yuv420p/)
		expect(probe.stderr).toMatch(/Audio: aac/)
		// Converting again numbers the file; the first is untouched.
		await convertInTool(zing, panel, heic, 'JPG')
		expect(readdirSync(downloads).sort()).toEqual(['IMG_0001 2.jpg', 'IMG_0001.jpg', 'clip.mp4'])
		expect(existsSync(heic)).toBe(true)
		await zing.close()
	})

	test('a document converts with Pandoc', async () => {
		const zing = await launchWithEngines()
		const md = join(zing.userDataDir, 'notes.md')
		writeFileSync(md, '# Title\n\nSome *text*.\n')
		const panel = await openConvert(zing)
		await convertInTool(zing, panel, md, 'Word')
		expect([...readFileSync(join(zing.userDataDir, 'downloads', 'notes.docx')).subarray(0, 2)]).toEqual([0x50, 0x4b])
		await zing.close()
	})

	// Open items from engine-rules.md, answered by running the engines.
	test('FFmpeg: a concat or playlist file renamed .mp4 is refused, and a job that does not name the input format is refused by the host', async () => {
		const zing = await launchWithEngines()
		const secrets = join(zing.userDataDir, 'secrets')
		mkdirSync(secrets, { recursive: true })
		const real = join(secrets, 'secret.mov')
		const filmed = spawnSync(ffmpegPath(zing), ['-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=duration=1:size=160x120:rate=10', '-c:v', 'libx264', real], { encoding: 'utf8' })
		expect(filmed.status, filmed.stderr).toBe(0)
		writeFileSync(join(secrets, 'concat.mp4'), "ffconcat version 1.0\nfile 'secret.mov'\n")
		writeFileSync(join(secrets, 'hls.mp4'), '#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:3\n#EXTINF:2.0,\nsecret.mov\n#EXT-X-ENDLIST\n')
		writeFileSync(join(secrets, 'network.mp4'), '#EXTM3U\n#EXTINF:2.0,\nhttp://127.0.0.1:9/x.ts\n#EXT-X-ENDLIST\n')
		await zing.chrome.evaluate(() => window.zing.openPanel('tool:Hashes', { x: 800, y: 300 }))
		const panelId = (await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels[0]!.id

		async function run(file: string, named: boolean): Promise<EngineExit> {
			await queuePick(zing, file)
			const [picked] = await zing.chrome.evaluate(() => window.zing.pickFiles(['any']))
			return zing.chrome.evaluate(
				async ([id, token, withFormat]) => {
					const exit = new Promise<EngineExit>((resolve) => {
						const stop = window.zing.onEngineExit((e) => {
							stop()
							resolve(e)
						})
					})
					await window.zing.runEngine('ffmpeg', id as string, { args: [...(withFormat ? ['-f', 'mp4'] : []), '-i', { input: token as string }, '-c:v', 'libx264', { output: 0 }], outputs: ['probe.mp4'] })
					return exit
				},
				[panelId, picked!.token, named] as const,
			)
		}

		// With the format named, as Convert does, every one is refused and nothing is written.
		for (const name of ['concat.mp4', 'hls.mp4', 'network.mp4']) {
			const exit = await run(join(secrets, name), true)
			expect(exit.code, name).not.toBe(0)
			expect(exit.outputs, name).toEqual([])
		}
		// Without it the host refuses the job: the whitelist ("file,pipe") alone did not stop a renamed concat file from reading
		// the files beside it (found with this engine), so the policy requires -f before every -i.
		for (const name of ['concat.mp4', 'hls.mp4', 'network.mp4']) await expect(run(join(secrets, name), false), name).rejects.toThrow(/-f and a format/)
		await zing.close()
	})

	test('ImageMagick: the policy is found through MAGICK_CONFIGURE_PATH and blocks the coders that reach files and addresses', async () => {
		const zing = await launchWithEngines()
		const png = join(zing.userDataDir, 'a.png')
		const made = spawnSync(join(zing.userDataDir, 'engines', 'magick', 'magick.exe'), ['-size', '8x8', 'xc:red', png], { encoding: 'utf8' })
		expect(made.status, made.stderr).toBe(0)
		writeFileSync(join(zing.userDataDir, 'list.png'), 'a.png\n')
		await zing.chrome.evaluate(() => window.zing.openPanel('tool:Hashes', { x: 800, y: 300 }))
		const panelId = (await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels[0]!.id
		await queuePick(zing, png)
		const [picked] = await zing.chrome.evaluate(() => window.zing.pickFiles(['any']))

		const runMagick = (id: string, token: string): Promise<EngineExit> =>
			zing.chrome.evaluate(
				async ([panel, file]) => {
					const done = new Promise<EngineExit>((resolve) => {
						const stop = window.zing.onEngineExit((e) => {
							stop()
							resolve(e)
						})
					})
					await window.zing.runEngine('magick', panel as string, { args: [{ input: file as string }, { output: 0 }], outputs: ['drawn.png'] })
					return done
				},
				[id, token] as const,
			)

		// The policy only has to matter if the flag list let a coder in; it lets none. What the policy file does when
		// ImageMagick starts is shown through the label coder, which the job cannot name but the policy lists.
		const directly = (args: string[], policy: boolean) =>
			spawnSync(join(zing.userDataDir, 'engines', 'magick', 'magick.exe'), args, { encoding: 'utf8', env: policy ? { ...process.env, MAGICK_CONFIGURE_PATH: join(zing.userDataDir, 'engine-jobs-policy') } : process.env })
		const policyDir = join(zing.userDataDir, 'engine-jobs-policy')
		mkdirSync(policyDir, { recursive: true })
		writeFileSync(join(policyDir, 'policy.xml'), readFileSync(join(import.meta.dirname, '..', 'host', 'Zing.Core', 'Engines', 'MagickPolicy.cs'), 'utf8').match(/<\?xml[\s\S]*?<\/policymap>/)![0])
		expect(directly(['label:hi', join(zing.userDataDir, 'l1.png')], false).status).toBe(0)
		const blocked = directly(['label:hi', join(zing.userDataDir, 'l2.png')], true)
		expect(blocked.status).not.toBe(0)
		expect(blocked.stderr).toContain('security policy')

		// Through the runner itself, which sets MAGICK_CONFIGURE_PATH: an SVG that embeds another file renders without the
		// policy (checked by hand) and is refused with it. That is why Convert refuses SVG input as well.
		const svg = join(zing.userDataDir, 'drawing.svg')
		writeFileSync(svg, '<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="8" height="8"><image xlink:href="a.png" width="8" height="8"/></svg>')
		await queuePick(zing, svg)
		const [drawing] = await zing.chrome.evaluate(() => window.zing.pickFiles(['any']))
		const refused = await runMagick(panelId, drawing!.token)
		expect(refused.code).not.toBe(0)
		expect(refused.stderrTail.join(' ')).toContain('security policy')
		expect(refused.outputs).toEqual([])

		// And a converted PNG works with the policy in force.
		const exit = await zing.chrome.evaluate(
			async ([id, token]) => {
				const done = new Promise<EngineExit>((resolve) => {
					const stop = window.zing.onEngineExit((e) => {
						stop()
						resolve(e)
					})
				})
				await window.zing.runEngine('magick', id as string, { args: [{ input: token as string }, '-delete', '1--1', '-auto-orient', { output: 0 }], outputs: ['b.jpg'] })
				return done
			},
			[panelId, picked!.token] as const,
		)
		expect(exit).toMatchObject({ code: 0, cancelled: false })
		expect(existsSync(join(zing.userDataDir, 'downloads', 'b.jpg'))).toBe(true)
		await zing.close()
	})

	test('odd-sized GIFs become MP4, big images become ICO and AVIF, and a Zing folder with % and [ in its path works', async () => {
		const dir = join(tmpdir(), `zing%d [real] ${Date.now()}`)
		mkdirSync(dir, { recursive: true })
		const zing = await launchWithEngines(dir, ['ffmpeg', 'magick'])
		const magick = join(zing.userDataDir, 'engines', 'magick', 'magick.exe')
		const inputs = join(zing.userDataDir, 'in[0]%d')
		mkdirSync(inputs, { recursive: true })
		const gif = join(inputs, 'odd.gif')
		expect(spawnSync(magick, ['-size', '33x31', 'xc:red', '-size', '33x31', 'xc:blue', gif], { encoding: 'utf8' }).status).toBe(0)
		const big = join(inputs, 'big.png')
		expect(spawnSync(magick, ['-size', '600x400', 'xc:red', big], { encoding: 'utf8' }).status).toBe(0)

		const panel = await openConvert(zing)
		await convertInTool(zing, panel, gif, 'MP4')
		await convertInTool(zing, panel, gif, 'PNG')
		await convertInTool(zing, panel, big, 'ICO')
		await convertInTool(zing, panel, big, 'AVIF')
		const downloads = join(zing.userDataDir, 'downloads')
		expect(readdirSync(downloads).sort()).toEqual(['big.avif', 'big.ico', 'odd.mp4', 'odd.png'])
		const probe = spawnSync(ffmpegPath(zing), ['-hide_banner', '-i', join(downloads, 'odd.mp4')], { encoding: 'utf8' })
		expect(probe.stderr).toMatch(/Video: h264 .* 32x30/)
		const ico = spawnSync(magick, ['identify', join(downloads, 'big.ico')], { encoding: 'utf8' })
		expect(ico.stdout).toMatch(/256x171/)
		expect(spawnSync(magick, ['identify', join(downloads, 'big.avif')], { encoding: 'utf8' }).stdout).toMatch(/600x400/)
		await zing.close()
	})

	test('ImageMagick: an SVG renamed .png is read by its content and still refused by the policy', async () => {
		const zing = await launchWithEngines(undefined, ['magick'])
		const secret = join(zing.userDataDir, 'secret.png')
		expect(spawnSync(join(zing.userDataDir, 'engines', 'magick', 'magick.exe'), ['-size', '8x8', 'xc:red', secret], { encoding: 'utf8' }).status).toBe(0)
		const disguised = join(zing.userDataDir, 'photo.png')
		writeFileSync(disguised, '<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="8" height="8"><image xlink:href="secret.png" width="8" height="8"/></svg>')
		await zing.chrome.evaluate(() => window.zing.openPanel('tool:Hashes', { x: 800, y: 300 }))
		const panelId = (await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels[0]!.id
		await queuePick(zing, disguised)
		const [picked] = await zing.chrome.evaluate(() => window.zing.pickFiles(['any']))
		const exit = await zing.chrome.evaluate(
			async ([id, token]) => {
				const done = new Promise<EngineExit>((resolve) => {
					const stop = window.zing.onEngineExit((e) => {
						stop()
						resolve(e)
					})
				})
				await window.zing.runEngine('magick', id as string, { args: [{ input: token as string }, { output: 0 }], outputs: ['out.jpg'] })
				return done
			},
			[panelId, picked!.token] as const,
		)
		expect(exit.code).not.toBe(0)
		expect(exit.outputs).toEqual([])
		expect(exit.stderrTail.join(' ')).toContain('security policy')
		await zing.close()
	})
})

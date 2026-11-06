import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import type { JobArg } from '../engines/engineJob'
import { ALL_CATALOG_EXTENSIONS, convertTargets, inputSupport, MAC_ONLY_INPUTS, type ConversionBackend } from './backends'
import { PDF_LIMITS } from '../pdf/pdfCore'
import { convertPlan, EVEN_SIZE_FILTER, explainFfmpegFailure, GIF_FILTER, ffmpegArgs, magickArgs, outputFileName, pageFileName, pandocArgs, safeFileBase, tarArgs, uniqueOutputName } from './commands'
import { allInputs, category, label } from './formatCatalog'

const token = { input: 'token-1' }
const out = { output: 0 }

describe('format catalog (ported)', () => {
	it('knows Krackn’s full set of input formats', () => expect(allInputs().size).toBe(ALL_CATALOG_EXTENSIONS.length))
	it('overlapping extensions resolve in Krackn’s order', () => {
		expect(category('gif')).toBe('video')
		expect(category('svg')).toBe('image')
		expect(category('obj')).toBe('model')
		expect(category('.MP3')).toBe('audio')
		expect(category('xyz')).toBeUndefined()
	})
	it('labels', () => {
		expect(label('msh')).toBe('Gmsh MSH (FEA)')
		expect(label('zst')).toBe('ZST')
	})
})

describe('numbered page names', () => {
	it('pdf pages are base-p001.png, three digits, with the file name made safe', () => {
		expect(pageFileName('My Book', 1, 'png')).toBe('My Book-p001.png')
		expect(pageFileName('a%b[1]', 100, 'jpg')).toBe('a_b_1_-p100.jpg')
		expect(pageFileName('x', 7, 'jpg')).toBe('x-p007.jpg')
	})
	it('refuses a page past the limit of pages in one run', () => {
		expect(() => pageFileName('x', PDF_LIMITS.maxConvertPages + 1, 'png')).toThrow(/pages/)
	})
})

describe('Windows coverage matrix', () => {
	const matrix = ALL_CATALOG_EXTENSIONS.map(inputSupport)
	const backends = new Set<string>()
	for (const entry of matrix) if ('readBy' in entry) backends.add(entry.readBy)
	for (const ext of ALL_CATALOG_EXTENSIONS) for (const target of convertTargets(ext)) backends.add(target.backend)

	it('every catalog format is either routed to a backend or explained', () => {
		for (const entry of matrix) expect('readBy' in entry || ('unavailable' in entry && entry.unavailable.length > 0)).toBe(true)
	})
	it('uses no sharp and no meshio: neither runs here', () => {
		const used = [...backends] as ConversionBackend[]
		expect(used).not.toContain('sharp' as ConversionBackend)
		expect(used).not.toContain('meshio' as ConversionBackend)
		expect(used.sort()).toEqual(['chromium-pdf', 'ffmpeg', 'imagemagick', 'pandoc', 'pdf-lib', 'pdfjs'])
	})
	it('HEIC and HEIF are read by ImageMagick', () => {
		expect(inputSupport('heic')).toMatchObject({ readBy: 'imagemagick' })
		expect(inputSupport('heif')).toMatchObject({ readBy: 'imagemagick' })
	})
	it('3D models, archives and Apple-only formats are explained, not offered', () => {
		for (const ext of ['stl', 'glb', 'zip', ...Object.keys(MAC_ONLY_INPUTS), 'doc']) {
			expect(inputSupport(ext), ext).toMatchObject({ unavailable: expect.stringMatching(/\S/) })
			expect(convertTargets(ext), ext).toEqual([])
		}
	})
	it('refuses what can read other files or addresses: SVG, playlists and scripts', () => {
		for (const ext of ['svg', 'm3u8', 'm3u', 'ffconcat', 'mvg', 'msl', 'ps', 'eps', 'sh', 'bat']) {
			expect(inputSupport(ext), ext).toHaveProperty('unavailable')
			expect(convertTargets(ext), ext).toEqual([])
		}
	})
	it('a text file is a document for Pandoc, never a playlist for FFmpeg', () => {
		expect(inputSupport('txt')).toMatchObject({ readBy: 'pandoc' })
	})
	it('audio and video inputs are read by FFmpeg only when its format can be named', () => {
		expect(inputSupport('mov')).toMatchObject({ readBy: 'ffmpeg' })
		expect(inputSupport('mp3')).toMatchObject({ readBy: 'ffmpeg' })
		expect(inputSupport('rmvb')).toHaveProperty('unavailable')
	})
	it('images offer image targets, videos offer video and audio, documents offer documents', () => {
		const exts = (input: string) => convertTargets(input).map((f) => f.ext)
		expect(exts('heic')).toEqual(expect.arrayContaining(['jpg', 'png', 'webp']))
		expect(exts('heic')).not.toContain('heic')
		expect(exts('mov')).toEqual(expect.arrayContaining(['mp4', 'mp3', 'gif']))
		expect(exts('mp3')).toEqual(expect.arrayContaining(['wav', 'flac']))
		expect(exts('mp3')).not.toContain('mp4')
		expect(exts('md')).toEqual(expect.arrayContaining(['docx', 'html', 'epub']))
	})
	it('a GIF is a picture and a clip: video and image targets, never audio', () => {
		const exts = convertTargets('gif').map((f) => f.ext)
		expect(exts).toEqual(expect.arrayContaining(['mp4', 'webm', 'png', 'jpg']))
		expect(exts).not.toContain('mp3')
		expect(exts).not.toContain('gif')
		expect(convertTargets('gif').find((f) => f.ext === 'png')).toMatchObject({ backend: 'imagemagick' })
		expect(convertTargets('gif').find((f) => f.ext === 'mp4')).toMatchObject({ backend: 'ffmpeg' })
	})
	it('PDF output is for HTML only, and goes through Chromium, not Pandoc', () => {
		expect(convertTargets('html').find((f) => f.ext === 'pdf')).toMatchObject({ backend: 'chromium-pdf' })
		expect(convertTargets('htm').some((f) => f.ext === 'pdf')).toBe(true)
		expect(convertTargets('md').some((f) => f.ext === 'pdf')).toBe(false)
	})
	it('a PDF is read by pdf.js and becomes PNG or JPEG pages, never a Word file and never an engine job', () => {
		expect(inputSupport('pdf')).toMatchObject({ readBy: 'pdfjs' })
		expect(convertTargets('pdf')).toEqual([
			expect.objectContaining({ ext: 'png', backend: 'pdfjs', lossy: false }),
			expect.objectContaining({ ext: 'jpg', backend: 'pdfjs', lossy: true }),
		])
		expect(convertTargets('pdf').some((f) => f.ext === 'docx')).toBe(false)
	})
	it('pictures PDF can hold are offered PDF, made by pdf-lib; others and documents are not', () => {
		for (const ext of ['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp']) expect(convertTargets(ext).find((f) => f.ext === 'pdf'), ext).toMatchObject({ backend: 'pdf-lib', lossy: false })
		for (const ext of ['heic', 'tiff', 'avif', 'ico', 'md', 'docx', 'mp4']) expect(convertTargets(ext).some((f) => f.ext === 'pdf'), ext).toBe(false)
	})
	it('never offers an output the host would refuse', () => {
		// Mirrors the extensions in host/Zing.Core/Engines/EngineFlagPolicy.cs.
		const allowed: Record<string, string[]> = {
			ffmpeg: ['mp4', 'mov', 'mkv', 'webm', 'mp3', 'wav', 'flac', 'aac', 'm4a', 'ogg', 'opus', 'gif'],
			imagemagick: ['jpg', 'jpeg', 'png', 'webp', 'gif', 'bmp', 'tif', 'tiff', 'avif', 'heic', 'ico'],
			pandoc: ['html', 'docx', 'odt', 'epub', 'rtf', 'tex', 'md', 'txt'],
			'chromium-pdf': ['pdf'],
			pdfjs: ['png', 'jpg'],
			'pdf-lib': ['pdf'],
		}
		for (const ext of ALL_CATALOG_EXTENSIONS) {
			for (const target of convertTargets(ext)) expect(allowed[target.backend], `${ext} to ${target.ext}`).toContain(outputFileName('x', target.ext).split('.').pop())
		}
	})
})

describe('ffmpeg arguments', () => {
	const run = (target: string, quality: number, input = 'mov') => ffmpegArgs(token, input, target, quality)
	const after = (args: readonly JobArg[], flag: string) => args[args.indexOf(flag) + 1]
	it('names the input format, then the file, and ends on the output', () => {
		const args = run('mp4', 50)
		expect(args.slice(0, 4)).toEqual(['-f', 'mov', '-i', token])
		expect(args.at(-1)).toEqual(out)
	})
	it('never uses -y or a filter that could read a file', () => {
		for (const target of ['mp4', 'gif', 'webm', 'mp3', 'prores']) {
			const args = run(target, 50)
			expect(args).not.toContain('-y')
			for (const arg of args) if (typeof arg === 'string') expect(arg).not.toMatch(/movie=|concat|lavfi|subtitles|:\/\//)
		}
	})
	it('mp3 VBR scale is inverted (best quality is 0)', () => {
		expect(after(run('mp3', 100, 'wav'), '-q:a')).toBe('0')
		expect(after(run('mp3', 0, 'wav'), '-q:a')).toBe('9')
	})
	it('audio targets never carry video, including long-tail formats', () => {
		for (const ext of ['opus', 'flac', 'mp3', 'wav', 'aac']) expect(run(ext, 50, 'mp4')).toContain('-vn')
	})
	it('opus and aac bitrates have floors', () => {
		expect(after(run('opus', 0), '-b:a')).toBe('32k')
		expect(after(run('m4a', 0), '-b:a')).toBe('64k')
	})
	it('H.264 CRF maps 0–100 quality onto 51–0 and stays playable everywhere', () => {
		expect(after(run('mp4', 100), '-crf')).toBe('0')
		expect(after(run('mp4', 0), '-crf')).toBe('51')
		// An iPhone clip is 10-bit HEVC; x264 would otherwise write High 10, which most players refuse.
		expect(after(run('mp4', 50), '-pix_fmt')).toBe('yuv420p')
	})
	it('H.264 gets the fixed even-size filter, since x264 refuses odd dimensions', () => {
		for (const target of ['mp4', 'mov', 'mkv']) expect(after(run(target, 50), '-vf')).toBe(EVEN_SIZE_FILTER)
		expect(run('webm', 50)).not.toContain('-vf')
	})
	it('GIF gets the fixed palette filter', () => expect(after(run('gif', 50), '-vf')).toContain('palettegen'))
	it('ProRes is written in a .mov', () => {
		expect(outputFileName('clip', 'prores')).toBe('clip.mov')
		expect(after(run('prores', 50), '-c:v')).toBe('prores_ks')
	})
	it('explains common failures', () => {
		expect(explainFfmpegFailure('Output file does not contain any stream', 'mp3')).toBe('this file has no audio track to extract')
		expect(explainFfmpegFailure('Invalid data found when processing input', 'mp4')).toContain('corrupt')
		expect(explainFfmpegFailure('line one\nConversion failed!\n', 'mp4')).toBe('Conversion failed!')
	})
})

describe('other backends', () => {
	it('magick always ends on the output, so an input can never be its destination', () => {
		for (const [target, quality] of [['jpg', 80], ['png', 80], ['webp', 0], ['gif', 100], ['ico', 50]] as const) {
			const args = magickArgs(token, target, quality)
			expect(args.at(-1)).toEqual(out)
			expect(args.filter((a) => typeof a === 'object')).toEqual([token, out])
		}
	})
	it('magick reads the file, then sets what applies to it', () => {
		expect(magickArgs(token, 'jpg', 80)).toEqual([token, '-delete', '1--1', '-auto-orient', '-quality', '80', out])
	})
	it('magick quality is clamped and only sent for lossy targets', () => {
		expect(magickArgs(token, 'jpg', 0)).toContain('1')
		expect(magickArgs(token, 'jpg', 500)).toContain('100')
		expect(magickArgs(token, 'png', 80)).not.toContain('-quality')
	})
	it('an icon is shrunk to the 256 pixels an icon can be, and only an icon', () => {
		expect(magickArgs(token, 'ico', 50)).toEqual([token, '-auto-orient', '-resize', '256x256>', out])
		expect(magickArgs(token, 'png', 50)).not.toContain('-resize')
	})
	it('multi-frame targets keep every frame', () => {
		expect(magickArgs(token, 'gif', 80)).not.toContain('-delete')
		expect(magickArgs(token, 'tiff', 80)).not.toContain('-delete')
	})
	it('pandoc names both formats, sandboxed options only, and ends on the output', () => {
		expect(pandocArgs(token, 'md', 'docx')).toEqual(['-f', 'markdown', token, '-t', 'docx', '--standalone', '-o', out])
		expect(pandocArgs(token, 'html', 'txt')).toEqual(['-f', 'html', token, '-t', 'plain', '-o', out])
		expect(() => pandocArgs(token, 'md', 'pdf')).toThrow(/Chromium/)
	})
	it('zip via tar.exe auto-compress', () => expect(tarArgs('C:\\d', 'f', 'C:\\o.zip', 'zip').slice(0, 2)).toEqual(['-a', '-c']))
})

describe('the plan for one conversion', () => {
	it('heic to jpg runs magick on the picked file, to one named output', () => {
		expect(convertPlan({ name: 'IMG_0001.HEIC', token: 'token-1' }, 'jpg', 85)).toEqual({
			kind: 'engine',
			engine: 'magick',
			args: [{ input: 'token-1' }, '-delete', '1--1', '-auto-orient', '-quality', '85', { output: 0 }],
			outputs: ['IMG_0001.jpg'],
		})
	})
	it('mov to mp4 runs ffmpeg with the input format named', () => {
		const plan = convertPlan({ name: 'clip.MOV', token: 'token-1' }, 'mp4', 60)
		expect(plan).toMatchObject({ kind: 'engine', engine: 'ffmpeg', outputs: ['clip.mp4'] })
		if (plan.kind === 'engine') expect(plan.args.slice(0, 4)).toEqual(['-f', 'mov', '-i', { input: 'token-1' }])
	})
	it('html to pdf is not an engine job: it is printed by Chromium', () => {
		expect(convertPlan({ name: 'page.html', token: 'token-1' }, 'pdf', 80)).toEqual({ kind: 'print', token: 'token-1', output: 'page.pdf' })
	})
	it('pdf to png or jpeg is pages drawn by pdf.js, named from the file', () => {
		expect(convertPlan({ name: 'My Book.PDF', token: 'token-1' }, 'png', 80)).toEqual({ kind: 'pages', token: 'token-1', base: 'My Book', target: 'png', quality: 80 })
		expect(convertPlan({ name: 'a.pdf', token: 't' }, 'jpg', 55)).toMatchObject({ kind: 'pages', target: 'jpg', quality: 55 })
	})
	it('a picture to pdf is made in the panel, not by an engine', () => {
		expect(convertPlan({ name: 'scan.png', token: 'token-1' }, 'pdf', 80)).toEqual({ kind: 'picture', token: 'token-1', output: 'scan.pdf' })
	})
	it('refuses an input the converter does not read, by name', () => {
		expect(() => convertPlan({ name: 'logo.svg', token: 't' }, 'png', 80)).toThrow(/SVG/)
		expect(() => convertPlan({ name: 'list.m3u8', token: 't' }, 'mp4', 80)).toThrow(/m3u8/)
		expect(() => convertPlan({ name: 'noextension', token: 't' }, 'mp4', 80)).toThrow(/extension/)
	})
	it('refuses a target the input cannot become', () => {
		expect(() => convertPlan({ name: 'a.mp3', token: 't' }, 'mp4', 80)).toThrow(/mp3.*mp4/)
	})
	it('refuses names ImageMagick would read as more than a file name', () => {
		expect(() => convertPlan({ name: 'pic[0].heic', token: 't' }, 'jpg', 80)).toThrow(/\[/)
		expect(() => convertPlan({ name: '100%.heic', token: 't' }, 'jpg', 80)).toThrow(/%/)
	})
	it('names the output from the input, cleaned', () => {
		expect(convertPlan({ name: 'my film é (1).heic', token: 't' }, 'png', 80)).toMatchObject({ outputs: ['my film é (1).png'] })
		expect(outputFileName('CON', 'png')).toBe('CON_.png')
	})
})

describe('output names', () => {
	it('a long name is cut so the file stays inside Windows path limits', () => {
		const name = outputFileName('é'.repeat(400), 'png')
		expect([...name].length).toBeLessThanOrEqual(100 + '.png'.length)
		expect(name.endsWith('.png')).toBe(true)
	})
	it('characters an engine reads as a pattern become underscores, so the file still converts', () => {
		expect(outputFileName('a%d [0] 100%', 'png')).toBe('a_d _0_ 100_.png')
		expect(convertPlan({ name: 'clip 100%.mov', token: 't' }, 'mp4', 80)).toMatchObject({ outputs: ['clip 100_.mp4'] })
	})
	it('more invisible and control characters are removed', () => {
		const odd = [0x061c, 0x80, 0x9f, 0x7f, 0x2028, 0x2029].map((code) => String.fromCodePoint(code)).join('x')
		expect(safeFileBase('a' + odd + 'b')).toBe('axxxxxb')
	})
	it('direction overrides and invisible controls are removed, so a name cannot disguise its extension', () => {
		expect(outputFileName('photo‮gpj.exe', 'png')).toBe('photogpj.exe.png')
		expect(safeFileBase('a‎b⁦c⁩d')).toBe('abcd')
	})
	const taken = new Set(['dup.pdf', 'dup 2.pdf'])
	it('never overwrites: numbers collisions', () => expect(uniqueOutputName('dup', 'pdf', (n) => taken.has(n), true)).toBe('dup 3.pdf'))
	it('honours turning the protection off', () => expect(uniqueOutputName('dup', 'pdf', (n) => taken.has(n), false)).toBe('dup.pdf'))
	it('three conversions of one file give three names', () => {
		const made = new Set<string>()
		for (let i = 0; i < 3; i++) made.add(uniqueOutputName('x', 'pdf', (n) => made.has(n), true))
		expect([...made]).toEqual(['x.pdf', 'x 2.pdf', 'x 3.pdf'])
	})
	it('strips characters Windows forbids and avoids reserved names', () => {
		expect(safeFileBase('a<b>:c"d/e\\f|g?h*')).toBe('abcdefgh')
		expect(safeFileBase('CON')).toBe('CON_')
		expect(safeFileBase('trailing dots... ')).toBe('trailing dots')
		expect(safeFileBase('???')).toBe('file')
	})
})

describe('the host allow-list', () => {
	// The host refuses any flag it does not list, so a flag used here and missing there fails at run time, not in review.
	const policy = readFileSync(new URL('../../../host/Zing.Core/Engines/EngineFlagPolicy.cs', import.meta.url), 'utf8')
	const flagsOf = (name: string, target: string): string[] => {
		const plan = convertPlan({ name, token: 't' }, target, 50)
		if (plan.kind !== 'engine') throw new Error('not an engine job')
		return plan.args.filter((arg): arg is string => typeof arg === 'string' && arg.startsWith('-') && arg !== '1--1')
	}
	const samples: [string, string][] = [['a.gif', 'png'], ['a.gif', 'mp4'], ['a.heic', 'ico'], ['a.mov', 'mp4'], ['a.mov', 'webm'], ['a.mov', 'prores'], ['a.mov', 'gif'], ['a.mov', 'mp3'], ['a.wav', 'ogg'], ['a.mp3', 'opus'], ['a.heic', 'jpg'], ['a.heic', 'gif'], ['a.md', 'docx'], ['a.html', 'txt']]
	it.each(samples)('%s to %s uses only flags the host lists', (name, target) => {
		for (const flag of flagsOf(name, target)) expect(policy, flag).toContain(`("${flag}"`)
	})
	it('the host accepts exactly the filters used here', () => {
		expect(policy).toContain(`GifFilter = "${GIF_FILTER}"`)
		expect(policy).toContain(`EvenSizeFilter = "${EVEN_SIZE_FILTER}"`)
	})
})

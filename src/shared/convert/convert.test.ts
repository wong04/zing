import { describe, expect, it } from 'vitest'
import { ALL_CATALOG_EXTENSIONS, inputSupport, isBuiltIn, MAC_ONLY_INPUTS, outputFormats } from './backends'
import { explainFfmpegFailure, ffmpegArgs, magickArgs, meshioArgs, pandocArgs, safeFileBase, tarArgs, uniqueOutputName } from './commands'
import { allInputs, category, label } from './formatCatalog'

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

describe('Windows coverage matrix', () => {
	const matrix = ALL_CATALOG_EXTENSIONS.map(inputSupport)
	const unavailable = matrix.filter((m) => 'unavailable' in m)
	it('every catalog format is either routed to a backend or explained', () => {
		for (const entry of matrix) expect('readBy' in entry || ('unavailable' in entry && entry.unavailable.length > 0)).toBe(true)
	})
	it('only Apple-only formats and legacy .doc are unavailable', () => {
		expect(unavailable.map((m) => m.ext).sort()).toEqual([...Object.keys(MAC_ONLY_INPUTS), 'doc'].sort())
	})
	it('common images need no install', () => {
		expect(outputFormats('image').filter((f) => isBuiltIn(f.backend)).map((f) => f.ext)).toEqual(expect.arrayContaining(['jpeg', 'png', 'webp', 'avif', 'pdf']))
	})
	it('document PDF goes through Chromium, not pandoc', () => expect(outputFormats('document')[0]).toMatchObject({ ext: 'pdf', backend: 'chromium-pdf' }))
	it('archives use the tar.exe built into Windows', () => expect(outputFormats('archive').every((f) => f.backend === 'tar')).toBe(true))
})

describe('ffmpeg arguments', () => {
	const tail = (args: string[]) => args.slice(3)
	it('mp3 VBR scale is inverted (best quality is 0)', () => {
		expect(tail(ffmpegArgs('in.wav', 'out.mp3', 'mp3', 100))).toEqual(['-vn', '-c:a', 'libmp3lame', '-q:a', '0', 'out.mp3'])
		expect(ffmpegArgs('in.wav', 'out.mp3', 'mp3', 0)).toContain('9')
	})
	it('audio targets never carry video, including long-tail formats', () => {
		for (const ext of ['opus', 'aiff', 'wv', 'flac']) expect(ffmpegArgs('in.mp4', `out.${ext}`, ext, 50)).toContain('-vn')
	})
	it('opus and aac bitrates have floors', () => {
		expect(ffmpegArgs('a', 'b.opus', 'opus', 0)).toContain('32k')
		expect(ffmpegArgs('a', 'b.m4a', 'm4a', 0)).toContain('64k')
	})
	it('H.264 CRF maps 0–100 quality onto 51–0', () => {
		const at = (q: number) => ffmpegArgs('a.mov', 'b.mp4', 'mp4', q)
		expect(at(100)[at(100).indexOf('-crf') + 1]).toBe('0')
		expect(at(0)[at(0).indexOf('-crf') + 1]).toBe('51')
	})
	it('GIF gets a generated palette', () => expect(ffmpegArgs('a.mp4', 'b.gif', 'gif', 50).join(' ')).toContain('palettegen'))
	it('ProRes is written to .mov', () => expect(ffmpegArgs('a.mp4', 'C:\\out\\b.prores', 'prores', 50).at(-1)).toBe('C:\\out\\b.mov'))
	it('explains common failures', () => {
		expect(explainFfmpegFailure('Output file does not contain any stream', 'mp3')).toBe('this file has no audio track to extract')
		expect(explainFfmpegFailure('Invalid data found when processing input', 'mp4')).toContain('corrupt')
		expect(explainFfmpegFailure('line one\nConversion failed!\n', 'mp4')).toBe('Conversion failed!')
	})
})

describe('other backends', () => {
	it('SVGs are rasterised at 300 dpi', () => expect(magickArgs('a.svg', 'b.png', 80).slice(0, 2)).toEqual(['-density', '300']))
	it('magick quality is clamped', () => expect(magickArgs('a.png', 'b.jpg', 0)).toContain('1'))
	it('pandoc standalone for epub/docx, never for pdf', () => {
		expect(pandocArgs('a.md', 'b.docx', 'docx')).toContain('--standalone')
		expect(pandocArgs('a.md', 'b.rst', 'rst')).not.toContain('--standalone')
		expect(() => pandocArgs('a.md', 'b.pdf', 'pdf')).toThrow(/printToPDF/)
	})
	it('.msh is written as Gmsh, not Fluent', () => expect(meshioArgs('a.stl', 'b.msh', 'msh').slice(-2)).toEqual(['--output-format', 'gmsh']))
	it('zip via tar.exe auto-compress', () => expect(tarArgs('C:\\d', 'f', 'C:\\o.zip', 'zip').slice(0, 2)).toEqual(['-a', '-c']))
})

describe('output names', () => {
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

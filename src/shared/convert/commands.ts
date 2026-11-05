// Argument lists for the external converters, ported from Krackn's ConversionEngine.swift. Pure, so every quality
// mapping is testable without running anything. Arguments are JobArgs for the host's EngineRunner: option text built
// from fixed constants here (never from a file's name or content), files as {input: token} and {output: n}. The
// host refuses any flag its EngineFlagPolicy does not list, so a flag added here is added there too.
import type { JobArg } from '../engines/engineJob'
import { convertTargets, ffmpegInputFormat, inputSupport, isLossyTarget, pandocReader } from './backends'
import { AUDIO } from './formatCatalog'

const isAudio = (ext: string) => (AUDIO as readonly string[]).includes(ext)

function clamp(value: number, min: number, max: number): number {
	return Math.max(min, Math.min(max, value))
}

const OUTPUT: JobArg = { output: 0 }

// The one filter FFmpeg is given: palette generation in a single graph, because its default 216-colour palette looks
// awful. Fixed text; the host accepts exactly this string for -vf.
export const GIF_FILTER = 'fps=12,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse'

// x264 refuses odd widths and heights (a GIF often has them); this rounds them down to even. Fixed text; the host accepts exactly it for -vf.
export const EVEN_SIZE_FILTER = 'scale=trunc(iw/2)*2:trunc(ih/2)*2'

// `quality` is 0-100 and only affects lossy targets. The input's format is named so FFmpeg never sniffs it.
export function ffmpegArgs(input: JobArg, inputFormat: string, ext: string, quality: number): JobArg[] {
	const codec = isAudio(ext) ? ['-vn', ...audioCodecArgs(ext, quality)] : videoCodecArgs(ext, quality)
	return ['-f', inputFormat, '-i', input, ...codec, OUTPUT]
}

function audioCodecArgs(ext: string, quality: number): string[] {
	switch (ext) {
		// libmp3lame's VBR scale is inverted: 0 is best, 9 worst.
		case 'mp3':
			return ['-c:a', 'libmp3lame', '-q:a', String(clamp(9 - Math.trunc((quality * 9) / 100), 0, 9))]
		case 'm4a':
		case 'aac':
			return ['-c:a', 'aac', '-b:a', `${Math.max(64, quality * 3)}k`]
		case 'wav':
			return ['-c:a', 'pcm_s16le']
		case 'flac':
			return ['-c:a', 'flac']
		case 'ogg':
			return ['-c:a', 'libvorbis', '-q:a', String(clamp(Math.trunc(quality / 10), 0, 10))]
		case 'opus':
			return ['-c:a', 'libopus', '-b:a', `${Math.max(32, quality * 2)}k`]
		default:
			throw new Error(`No audio settings for .${ext}`)
	}
}

function videoCodecArgs(ext: string, quality: number): string[] {
	switch (ext) {
		case 'gif':
			return ['-vf', GIF_FILTER, '-loop', '0']
		case 'webm':
			return ['-c:v', 'libvpx-vp9', '-crf', String(63 - Math.trunc((quality * 63) / 100)), '-b:v', '0', '-pix_fmt', 'yuv420p', '-c:a', 'libopus']
		case 'prores':
			return ['-c:v', 'prores_ks', '-profile:v', '3', '-c:a', 'pcm_s16le']
		// H.264 CRF runs 0 (lossless) to 51 (worst). yuv420p keeps a 10-bit iPhone clip playable in ordinary players.
		case 'mp4':
		case 'mov':
		case 'mkv':
			return ['-vf', EVEN_SIZE_FILTER, '-c:v', 'libx264', '-crf', String(clamp(51 - Math.trunc((quality * 51) / 100), 0, 51)), '-preset', 'medium', '-pix_fmt', 'yuv420p', '-c:a', 'aac']
		default:
			throw new Error(`No video settings for .${ext}`)
	}
}

// ffmpeg's stderr is noisy and its last line is often just "Conversion
// failed!". Translate the common causes into something a user can act on.
export function explainFfmpegFailure(stderr: string, target: string): string {
	if (stderr.includes('does not contain any stream') || stderr.includes('Output file does not contain')) {
		return isAudio(target) ? 'this file has no audio track to extract' : 'nothing to convert into that format'
	}
	if (stderr.includes('Unknown encoder') || stderr.includes('Encoder not found')) return "this build of FFmpeg doesn't include that codec"
	if (stderr.includes('Invalid data found')) return "the file appears to be corrupt or isn't the format its name claims"
	if (stderr.includes('No such file')) return 'the source file is missing'
	return lastLine(stderr)
}

export function lastLine(text: string): string {
	const lines = text.split(/\r?\n/).filter((l) => l.trim() !== '')
	return lines.at(-1) ?? 'unknown error'
}

// Targets whose files hold several frames; every other target gets the first frame only, since ImageMagick
// otherwise writes "name-0.jpg", "name-1.jpg" and the one expected output never appears.
const MULTI_FRAME = new Set(['gif', 'tiff', 'tif', 'webp', 'ico'])

// The input comes first and the output last. The host does not shape-check a bare argument, and ImageMagick
// overwrites silently, so the last argument is always {output: 0}.
export function magickArgs(input: JobArg, ext: string, quality: number): JobArg[] {
	const firstFrame = MULTI_FRAME.has(ext) ? [] : ['-delete', '1--1']
	const lossy = isLossyTarget(ext) ? ['-quality', String(clamp(Math.trunc(quality), 1, 100))] : []
	// An icon holds at most 256 by 256 pixels; "256x256>" only ever shrinks.
	const iconSize = ext === 'ico' ? ['-resize', '256x256>'] : []
	return [input, ...firstFrame, '-auto-orient', ...lossy, ...iconSize, OUTPUT]
}

// Pandoc can't infer a standalone document for these from the extension alone.
const STANDALONE = new Set(['epub', 'tex', 'html', 'docx', 'odt'])
const PANDOC_WRITERS: Readonly<Record<string, string>> = { md: 'markdown', txt: 'plain', tex: 'latex', html: 'html', docx: 'docx', odt: 'odt', epub: 'epub', rtf: 'rtf' }

export function pandocArgs(input: JobArg, inputExt: string, ext: string): JobArg[] {
	if (ext === 'pdf') throw new Error('PDF output goes through Chromium printToPDF, not pandoc (pandoc needs a LaTeX engine for PDF)')
	const reader = pandocReader(inputExt)
	if (reader === undefined) throw new Error(`Pandoc has no reader for .${inputExt}`)
	const writer = PANDOC_WRITERS[ext]
	if (writer === undefined) throw new Error(`Pandoc has no writer for .${ext}`)
	return ['-f', reader, input, '-t', writer, ...(STANDALONE.has(ext) ? ['--standalone'] : []), '-o', OUTPUT]
}

// Windows 10+ ships bsdtar as tar.exe; `-a` picks the compressor from the
// extension, which is how it writes .zip.
export function tarArgs(sourceDir: string, sourceName: string, destination: string, ext: 'zip' | 'tar' | 'gz'): string[] {
	switch (ext) {
		case 'zip':
			return ['-a', '-c', '-f', destination, '-C', sourceDir, sourceName]
		case 'tar':
			return ['-c', '-f', destination, '-C', sourceDir, sourceName]
		case 'gz':
			return ['-c', '-z', '-f', destination, '-C', sourceDir, sourceName]
	}
}

// Never silently clobber an existing file: append " 2", " 3"... Honours the
// "Never overwrite existing files" preference (preserveOriginals), which in
// Krackn was once stored and never read. Convert itself lets the host number the
// file (EngineRunner places outputs without overwriting, so two jobs can't race).
export function uniqueOutputName(base: string, ext: string, exists: (name: string) => boolean, preserveOriginals: boolean): string {
	const first = `${base}.${ext}`
	if (!preserveOriginals) return first
	let candidate = first
	let counter = 2
	while (exists(candidate)) {
		candidate = `${base} ${counter}.${ext}`
		counter++
	}
	return candidate
}

// Windows forbids these in file names, and a converted file named after a
// page title or URL can contain any of them.
// Delete and C1 controls, the Arabic letter mark, zero-width characters, line and paragraph separators, direction marks and embeddings/overrides/isolates, and the byte-order mark.
const INVISIBLE: readonly (readonly [number, number])[] = [[0x7f, 0x9f], [0x61c, 0x61c], [0x200b, 0x200f], [0x2028, 0x202e], [0x2060, 0x2069], [0xfeff, 0xfeff]]
const isInvisible = (code: number): boolean => INVISIBLE.some(([from, to]) => code >= from && code <= to)

export function safeFileBase(name: string): string {
	// Control characters, and the invisible ones that turn text around (a right-to-left override can make "photo-gpj.exe" read as "photo-exe.jpg").
	const printable = [...name].filter((ch) => ch.charCodeAt(0) >= 0x20 && !isInvisible(ch.charCodeAt(0))).join('')
	const cleaned = printable.replace(/[<>:"/\\|?*]/g, '').replace(/[. ]+$/, '').trim()
	const reserved = /^(con|prn|aux|nul|com[0-9]|lpt[0-9])$/i
	if (!cleaned) return 'file'
	return reserved.test(cleaned) ? `${cleaned}_` : cleaned
}

export class ConvertRefusedError extends Error {
	override name = 'ConvertRefusedError'
}

export type ConvertPlan =
	| { readonly kind: 'engine'; readonly engine: 'ffmpeg' | 'magick' | 'pandoc'; readonly args: JobArg[]; readonly outputs: string[] }
	| { readonly kind: 'print'; readonly token: string; readonly output: string }

// ProRes lives in a .mov container; there is no .prores extension.
function outputExtension(ext: string): string {
	return ext === 'prores' ? 'mov' : ext
}

// The job folder, Zing's data folder and the extension all count towards Windows' 260-character path limit.
const MAX_BASE_CHARACTERS = 100

/** The name an output is asked for; the host numbers it if the Downloads folder has one already. */
export function outputFileName(base: string, ext: string): string {
	// "%" and brackets are patterns to ImageMagick and yt-dlp (the host refuses them in an output name), so they become "_".
	const plain = safeFileBase(base).replace(/[%[\]]/g, '_')
	return `${safeFileBase([...plain].slice(0, MAX_BASE_CHARACTERS).join(''))}.${outputExtension(ext)}`
}

function splitName(name: string): { base: string; ext: string } {
	const dot = name.lastIndexOf('.')
	if (dot <= 0 || dot === name.length - 1) throw new ConvertRefusedError(`"${name}" has no extension, so Zing can't tell what it is`)
	return { base: name.slice(0, dot), ext: name.slice(dot + 1).toLowerCase() }
}

// ImageMagick reads "[0]" after a file name as a frame choice and "%" as an escape, in the name it is given.
function checkMagickName(name: string): void {
	for (const ch of ['[', ']', '%']) {
		if (name.includes(ch)) throw new ConvertRefusedError(`"${name}" has "${ch}" in its name, which ImageMagick reads as an instruction. Rename the file first`)
	}
}

/**
 * What to run to turn the picked file into `target`.
 * @throws ConvertRefusedError the input or target is not one the converter offers, or the file's name would be misread.
 */
export function convertPlan(input: { readonly name: string; readonly token: string }, target: string, quality: number): ConvertPlan {
	const { base, ext } = splitName(input.name)
	const support = inputSupport(ext)
	if (!('readBy' in support)) throw new ConvertRefusedError(`"${input.name}" can't be converted: ${support.unavailable}`)
	const format = convertTargets(ext).find((f) => f.ext === target)
	if (!format) throw new ConvertRefusedError(`A .${ext} file can't become .${target}`)
	const output = outputFileName(base, target)
	const file: JobArg = { input: input.token }
	switch (format.backend) {
		case 'imagemagick':
			checkMagickName(input.name)
			return { kind: 'engine', engine: 'magick', args: magickArgs(file, target, quality), outputs: [output] }
		case 'ffmpeg':
			return { kind: 'engine', engine: 'ffmpeg', args: ffmpegArgs(file, ffmpegInputFormat(ext)!, target, quality), outputs: [output] }
		case 'pandoc':
			return { kind: 'engine', engine: 'pandoc', args: pandocArgs(file, ext, target), outputs: [output] }
		case 'chromium-pdf':
			return { kind: 'print', token: input.token, output }
		case 'tar':
			throw new ConvertRefusedError(`"${input.name}" can't be converted here`)
	}
}

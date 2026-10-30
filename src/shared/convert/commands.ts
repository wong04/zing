// Argument lists for the external converters, ported from Krackn's
// ConversionEngine.swift. Pure, so every quality mapping is testable without
// running ffmpeg. Arguments are always arrays passed to spawn without a
// shell, never a command string.
import { AUDIO } from './formatCatalog'

const isAudio = (ext: string) => (AUDIO as readonly string[]).includes(ext)

function clamp(value: number, min: number, max: number): number {
	return Math.max(min, Math.min(max, value))
}

// `quality` is 0–100 and only affects lossy targets.
export function ffmpegArgs(source: string, destination: string, ext: string, quality: number): string[] {
	const args = ['-y', '-i', source]
	// Route by what the *target* is. Krackn once switched on a hard-coded list
	// of extensions, so any audio format not named (opus, aiff, wv...) fell
	// through to the H.264 branch and produced an empty file.
	if (isAudio(ext)) {
		args.push('-vn', ...audioCodecArgs(ext, quality))
	} else {
		args.push(...videoCodecArgs(ext, quality))
	}
	args.push(ffmpegDestination(destination, ext))
	return args
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
		case 'alac':
			return ['-c:a', 'alac']
		case 'ogg':
			return ['-c:a', 'libvorbis', '-q:a', String(clamp(Math.trunc(quality / 10), 0, 10))]
		case 'opus':
			return ['-c:a', 'libopus', '-b:a', `${Math.max(32, quality * 2)}k`]
		case 'aiff':
		case 'aif':
			return ['-c:a', 'pcm_s16be']
		// The container's default encoder is what makes the long tail work.
		default:
			return []
	}
}

function videoCodecArgs(ext: string, quality: number): string[] {
	switch (ext) {
		// Palette generation in one filtergraph; ffmpeg's default 216-colour
		// palette looks awful.
		case 'gif':
			return ['-vf', 'fps=12,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse', '-loop', '0']
		case 'webm':
			return ['-c:v', 'libvpx-vp9', '-crf', String(63 - Math.trunc((quality * 63) / 100)), '-b:v', '0', '-c:a', 'libopus']
		case 'prores':
			return ['-c:v', 'prores_ks', '-profile:v', '3', '-c:a', 'pcm_s16le']
		// H.264 CRF runs 0 (lossless) to 51 (worst).
		case 'mp4':
		case 'mov':
		case 'mkv':
		case 'm4v':
		case 'avi':
		case 'flv':
		case 'ts':
		case '3gp':
			return ['-c:v', 'libx264', '-crf', String(clamp(51 - Math.trunc((quality * 51) / 100), 0, 51)), '-preset', 'medium', '-c:a', 'aac']
		default:
			return []
	}
}

// ProRes lives in a .mov container; there is no .prores extension.
export function ffmpegDestination(destination: string, ext: string): string {
	if (ext !== 'prores') return destination
	return destination.replace(/\.prores$/i, '.mov')
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

export function magickArgs(source: string, destination: string, quality: number): string[] {
	// Rasterising a vector at its tiny default density looks broken, so render
	// SVGs at print resolution.
	const density = /\.svg$/i.test(source) ? ['-density', '300'] : []
	return [...density, source, '-quality', String(clamp(quality, 1, 100)), destination]
}

// Pandoc can't infer a standalone document for these from the extension alone.
const STANDALONE = new Set(['epub', 'tex', 'html', 'docx', 'odt'])

export function pandocArgs(source: string, destination: string, ext: string): string[] {
	if (ext === 'pdf') throw new Error('PDF output goes through Chromium printToPDF, not pandoc (pandoc needs a LaTeX engine for PDF)')
	return [source, '-o', destination, ...(STANDALONE.has(ext) ? ['--standalone'] : [])]
}

// Several extensions are ambiguous: ".msh" alone means Gmsh to most
// engineers, but meshio defaults it to Ansys Fluent. Naming the format
// explicitly avoids silently writing the wrong one.
const MESHIO_FORMATS: Record<string, string> = { msh: 'gmsh', inp: 'abaqus', bdf: 'nastran', fem: 'nastran', nas: 'nastran', e: 'exodus', exo: 'exodus', dat: 'tecplot' }

export function meshioFormat(ext: string): string | undefined {
	return MESHIO_FORMATS[ext.toLowerCase()]
}

// meshio through uv, so it never needs a Python install the user manages.
export function meshioArgs(source: string, destination: string, ext: string): string[] {
	const explicit = meshioFormat(ext)
	return ['tool', 'run', '--from', 'meshio', 'meshio', 'convert', source, destination, ...(explicit ? ['--output-format', explicit] : [])]
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
// Krackn was once stored and never read.
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
export function safeFileBase(name: string): string {
	const printable = [...name].filter((ch) => ch.charCodeAt(0) >= 0x20).join('')
	const cleaned = printable.replace(/[<>:"/\\|?*]/g, '').replace(/[. ]+$/, '').trim()
	const reserved = /^(con|prn|aux|nul|com[0-9]|lpt[0-9])$/i
	if (!cleaned) return 'file'
	return reserved.test(cleaned) ? `${cleaned}_` : cleaned
}

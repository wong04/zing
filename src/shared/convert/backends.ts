import { AUDIO, BASIC_IMAGES, category, DOCUMENTS, label, MAGICK_IMAGES, MESHES, MODEL_INTERCHANGE, VIDEO, ARCHIVES, type ConversionCategory } from './formatCatalog'

// Windows replacements for Krackn's macOS backends:
// sips -> sharp (bundled) + ImageMagick, textutil -> pandoc,
// PDFKit -> Chromium's printToPDF, ditto/tar -> Windows' built-in tar.exe
// (bsdtar), Krackn's private Python venv -> meshio run through uv.
export type ConversionBackend = 'sharp' | 'imagemagick' | 'ffmpeg' | 'pandoc' | 'chromium-pdf' | 'meshio' | 'tar'

export interface ConversionFormat {
	ext: string
	label: string
	backend: ConversionBackend
}

// Ships with Zing (sharp) or with Windows (tar.exe, Chromium); everything
// else is installed on demand through winget.
export function isBuiltIn(backend: ConversionBackend): boolean {
	return backend === 'sharp' || backend === 'chromium-pdf' || backend === 'tar'
}

export const WINGET_IDS: Record<Exclude<ConversionBackend, 'sharp' | 'chromium-pdf' | 'tar'>, string> = {
	imagemagick: 'ImageMagick.ImageMagick',
	ffmpeg: 'Gyan.FFmpeg',
	pandoc: 'JohnMacFarlane.Pandoc',
	meshio: 'astral-sh.uv',
}

// sharp's prebuilt libvips writes these without any install.
const SHARP_OUTPUTS = new Set(['jpeg', 'jpg', 'png', 'webp', 'avif', 'tiff', 'tif', 'gif'])

// Apple-only formats with no reader on Windows. Listed rather than silently
// missing, so the converter can say why.
export const MAC_ONLY_INPUTS: Record<string, string> = {
	rtfd: 'RTFD is a macOS bundle format with no Windows reader',
	pages: 'Apple Pages documents can only be opened by Pages',
	key: 'Apple Keynote presentations can only be opened by Keynote',
	numbers: 'Apple Numbers spreadsheets can only be opened by Numbers',
	webarchive: 'Safari web archives have no Windows reader',
	pict: 'PICT is a retired Mac image format ImageMagick no longer reads on Windows',
}

function format(ext: string, backend: ConversionBackend, customLabel?: string): ConversionFormat {
	return { ext, label: customLabel ?? label(ext), backend }
}

export function outputFormats(cat: ConversionCategory): ConversionFormat[] {
	switch (cat) {
		case 'image':
			return [
				...BASIC_IMAGES.filter((e) => e !== 'pict').map((e) => format(e, SHARP_OUTPUTS.has(e) ? 'sharp' : 'imagemagick')),
				...MAGICK_IMAGES.map((e) => format(e, SHARP_OUTPUTS.has(e) ? 'sharp' : 'imagemagick')),
				format('pdf', 'chromium-pdf', 'PDF (combined)'),
			]
		case 'document':
			return [
				format('pdf', 'chromium-pdf'),
				format('txt', 'pandoc', 'Plain text'),
				format('rtf', 'pandoc', 'RTF'),
				format('html', 'pandoc', 'HTML'),
				format('docx', 'pandoc', 'Word (.docx)'),
				...['md', 'epub', 'tex', 'rst', 'odt', 'org', 'asciidoc', 'docbook', 'opml', 'fb2', 'man', 'csv', 'json', 'xml'].map((e) => format(e, 'pandoc')),
			]
		case 'audio':
			return AUDIO.map((e) => format(e, 'ffmpeg'))
		case 'video':
			return VIDEO.map((e) => format(e, 'ffmpeg'))
		case 'model':
			return MESHES.map((e) => format(e, 'meshio'))
		case 'archive':
			return ['zip', 'tar', 'gz'].map((e) => format(e, 'tar'))
	}
}

export type InputSupport = { ext: string; category: ConversionCategory; readBy: ConversionBackend } | { ext: string; category: ConversionCategory | undefined; unavailable: string }

const PANDOC_READS = new Set(['docx', 'odt', 'rtf', 'txt', 'md', 'markdown', 'html', 'htm', 'xhtml', 'epub', 'tex', 'latex', 'org', 'rst', 'wiki', 'textile', 'asciidoc', 'adoc', 'opml', 'fb2', 'docbook', 'jats', 'man', 'csv', 'tsv', 'json', 'pptx', 'xlsx', 'ods', 'ms', 'icml', 'yaml', 'xml'])

// Which backend reads each of the catalog's input formats on Windows. This
// is the coverage matrix the parity audit checks against Krackn's 196.
export function inputSupport(ext: string): InputSupport {
	const cat = category(ext)
	const macOnly = MAC_ONLY_INPUTS[ext]
	if (macOnly) return { ext, category: cat, unavailable: macOnly }
	if (!cat) return { ext, category: undefined, unavailable: 'Not a format the converter knows' }
	if (cat === 'image') return { ext, category: cat, readBy: SHARP_OUTPUTS.has(ext) ? 'sharp' : 'imagemagick' }
	if (cat === 'audio' || cat === 'video') return { ext, category: cat, readBy: 'ffmpeg' }
	if (cat === 'model') return { ext, category: cat, readBy: 'meshio' }
	if (cat === 'archive') return { ext, category: cat, readBy: 'tar' }
	if (ext === 'pdf') return { ext, category: cat, readBy: 'chromium-pdf' }
	if (ext === 'doc') return { ext, category: cat, unavailable: 'Legacy .doc needs Word or LibreOffice; pandoc only reads .docx' }
	if (PANDOC_READS.has(ext)) return { ext, category: cat, readBy: 'pandoc' }
	return { ext, category: cat, unavailable: `pandoc has no ${ext} reader` }
}

export const ALL_CATALOG_EXTENSIONS: readonly string[] = [...new Set<string>([...BASIC_IMAGES, ...MAGICK_IMAGES, ...AUDIO, ...VIDEO, ...DOCUMENTS, ...MESHES, ...MODEL_INTERCHANGE, ...ARCHIVES])]

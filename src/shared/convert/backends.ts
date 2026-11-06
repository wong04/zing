import { AUDIO, BASIC_IMAGES, category, DOCUMENTS, MAGICK_IMAGES, MESHES, MODEL_INTERCHANGE, VIDEO, ARCHIVES, label, type ConversionCategory } from './formatCatalog'

// Which program reads and writes each format on Windows. Krackn's macOS backends are replaced by ImageMagick (sips),
// Pandoc (textutil), Chromium's PrintToPdfAsync (PDFKit) and FFmpeg. The downloaded engines are run by the host's
// EngineRunner (host/Zing.Core/Engines), which accepts only the flags and output extensions in its EngineFlagPolicy.
// There is no sharp (it isn't bundled) and no meshio (it needs Python), so 3D models are listed as unavailable
// rather than silently missing.
export type ConversionBackend = 'imagemagick' | 'ffmpeg' | 'pandoc' | 'chromium-pdf' | 'pdfjs' | 'pdf-lib' | 'tar'

export interface ConversionFormat {
	ext: string
	label: string
	backend: ConversionBackend
	/** Whether a quality setting changes the result. */
	lossy: boolean
}

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

// FFmpeg is told each input's format (`-f`) instead of guessing from the file's content, so a playlist renamed
// "clip.mp4" fails rather than being followed. Every name here is in the host's `-f` list.
const FFMPEG_INPUT_FORMATS: Readonly<Record<string, string>> = {
	mp4: 'mp4', m4v: 'mp4', '3gp': 'mp4', '3g2': 'mp4', m4a: 'mp4', mov: 'mov',
	mkv: 'matroska', mka: 'matroska', webm: 'webm',
	avi: 'avi', flv: 'flv', wmv: 'asf', wma: 'asf', asf: 'asf', mpg: 'mpeg', mpeg: 'mpeg',
	ts: 'mpegts', mts: 'mpegts', m2ts: 'mpegts', ogg: 'ogg', ogv: 'ogg', opus: 'ogg', gif: 'gif',
	mp3: 'mp3', wav: 'wav', flac: 'flac', aac: 'aac', ac3: 'ac3', amr: 'amr', aiff: 'aiff', aif: 'aiff',
}

export function ffmpegInputFormat(ext: string): string | undefined {
	return FFMPEG_INPUT_FORMATS[ext.toLowerCase()]
}

// Pandoc readers by extension; every name is in the host's Pandoc format list. A plain text file is read as Markdown.
const PANDOC_READERS: Readonly<Record<string, string>> = {
	md: 'markdown', markdown: 'markdown', txt: 'markdown', html: 'html', htm: 'html', xhtml: 'html',
	docx: 'docx', odt: 'odt', epub: 'epub', rtf: 'rtf', tex: 'latex', latex: 'latex',
}

export function pandocReader(ext: string): string | undefined {
	return PANDOC_READERS[ext.toLowerCase()]
}

// What the converter refuses outright. SVG can name other files and web addresses; ImageMagick's policy is a second
// line of defence, not a reason to trust the file.
const REFUSED_INPUTS: Readonly<Record<string, string>> = {
	svg: 'SVG files can point at other files and web addresses, so Zing does not open them for conversion',
	doc: 'Legacy .doc needs Word or LibreOffice; Pandoc only reads .docx',
}

function format(ext: string, backend: ConversionBackend, lossy: boolean, customLabel?: string): ConversionFormat {
	return { ext, label: customLabel ?? label(ext), backend, lossy }
}

// ImageMagick's portable build reads HEIC but cannot write it ("no encode delegate"), so HEIC is not a target.
const IMAGE_TARGETS: readonly ConversionFormat[] = [
	...['jpg', 'png', 'webp', 'avif', 'gif', 'bmp'].map((ext) => format(ext, 'imagemagick', ['jpg', 'webp', 'avif'].includes(ext))),
	format('tiff', 'imagemagick', false),
	format('ico', 'imagemagick', false),
]

const AUDIO_TARGETS: readonly ConversionFormat[] = [
	...['mp3', 'm4a', 'aac', 'ogg', 'opus'].map((ext) => format(ext, 'ffmpeg', true)),
	...['wav', 'flac'].map((ext) => format(ext, 'ffmpeg', false)),
]

const VIDEO_TARGETS: readonly ConversionFormat[] = [
	...['mp4', 'mov', 'mkv', 'webm'].map((ext) => format(ext, 'ffmpeg', true)),
	format('gif', 'ffmpeg', false),
	format('prores', 'ffmpeg', false),
]

const DOCUMENT_TARGETS: readonly ConversionFormat[] = [
	format('txt', 'pandoc', false, 'Plain text'),
	format('rtf', 'pandoc', false, 'RTF'),
	format('html', 'pandoc', false, 'HTML'),
	format('docx', 'pandoc', false, 'Word (.docx)'),
	format('md', 'pandoc', false),
	format('epub', 'pandoc', false),
	format('tex', 'pandoc', false),
	format('odt', 'pandoc', false, 'OpenDocument (.odt)'),
]

// PDF comes from HTML (Chromium prints the page; Pandoc would need a LaTeX engine) and from pictures (pdf-lib, in the panel).
// ImageMagick's PDF coder is switched off by the engine policy, so it is never asked to write one.
const HTML_TARGETS: readonly ConversionFormat[] = [format('pdf', 'chromium-pdf', false), ...DOCUMENT_TARGETS]

// The pictures a viewer session reads (PNG, JPEG, WebP): pdf-lib places PNG and JPEG as they are, and the browser decodes WebP first.
const PICTURES_FOR_PDF: ReadonlySet<string> = new Set(['png', 'jpg', 'jpeg', 'webp'])
const PICTURE_TO_PDF: ConversionFormat = format('pdf', 'pdf-lib', false)

// A PDF is drawn page by page by pdf.js and saved as one picture per page. There is no Word target: Pandoc can't read PDFs.
const PDF_TARGETS: readonly ConversionFormat[] = [format('png', 'pdfjs', false), format('jpg', 'pdfjs', true)]

// Two extensions that are one format, so a file is never offered as a conversion to itself.
const SAME_FORMAT: Readonly<Record<string, string>> = { jpeg: 'jpg', tif: 'tiff', htm: 'html', markdown: 'md', latex: 'tex' }

const sameFormat = (a: string, b: string): boolean => (SAME_FORMAT[a] ?? a) === (SAME_FORMAT[b] ?? b)

// What an input could become, by the backend that reads it. To offer more (PDF to PNG, say), give the reader its targets here.
function targetsFor(ext: string, readBy: ConversionBackend): readonly ConversionFormat[] {
	switch (readBy) {
		case 'imagemagick':
			return PICTURES_FOR_PDF.has(ext) ? [...IMAGE_TARGETS, PICTURE_TO_PDF] : IMAGE_TARGETS
		case 'pdfjs':
			return PDF_TARGETS
		case 'ffmpeg':
			if (ext === 'gif') return [...VIDEO_TARGETS, ...IMAGE_TARGETS]
			return category(ext) === 'audio' ? AUDIO_TARGETS : [...VIDEO_TARGETS, ...AUDIO_TARGETS]
		case 'pandoc':
			return ext === 'html' || ext === 'htm' || ext === 'xhtml' ? HTML_TARGETS : DOCUMENT_TARGETS
		case 'chromium-pdf':
		case 'pdf-lib':
		case 'tar':
			return []
	}
}

/** The formats a file can be converted to; none for an input the converter does not read. */
export function convertTargets(inputExt: string): ConversionFormat[] {
	const ext = inputExt.toLowerCase().replace(/^\./, '')
	const support = inputSupport(ext)
	if (!('readBy' in support)) return []
	return targetsFor(ext, support.readBy).filter((target) => !sameFormat(target.ext, ext))
}

export type InputSupport = { ext: string; category: ConversionCategory; readBy: ConversionBackend } | { ext: string; category: ConversionCategory | undefined; unavailable: string }

/** Which backend reads each of the catalog's input formats on Windows, or why none does. The coverage matrix the parity audit checks against Krackn's 196. */
export function inputSupport(extension: string): InputSupport {
	const ext = extension.toLowerCase().replace(/^\./, '')
	const cat = category(ext)
	const macOnly = MAC_ONLY_INPUTS[ext]
	if (macOnly) return { ext, category: cat, unavailable: macOnly }
	const refused = REFUSED_INPUTS[ext]
	if (refused) return { ext, category: cat, unavailable: refused }
	if (!cat) return { ext, category: undefined, unavailable: `Not a format the converter knows (.${ext})` }
	if (cat === 'model') return { ext, category: cat, unavailable: '3D models need Python tooling that Zing does not ship' }
	if (cat === 'archive') return { ext, category: cat, unavailable: 'Archives are not converted here' }
	if (ext === 'pdf') return { ext, category: cat, readBy: 'pdfjs' }
	if (cat === 'image') return { ext, category: cat, readBy: 'imagemagick' }
	if (cat === 'audio' || cat === 'video') {
		if (ffmpegInputFormat(ext) === undefined) return { ext, category: cat, unavailable: `Zing can't yet check that a .${ext} file really is one before FFmpeg reads it` }
		return { ext, category: cat, readBy: 'ffmpeg' }
	}
	if (pandocReader(ext) !== undefined) return { ext, category: cat, readBy: 'pandoc' }
	return { ext, category: cat, unavailable: `Pandoc has no .${ext} reader in Zing yet` }
}

export const ALL_CATALOG_EXTENSIONS: readonly string[] = [...new Set<string>([...BASIC_IMAGES, ...MAGICK_IMAGES, ...AUDIO, ...VIDEO, ...DOCUMENTS, ...MESHES, ...MODEL_INTERCHANGE, ...ARCHIVES])]

const ALL_TARGETS: readonly ConversionFormat[] = [...IMAGE_TARGETS, ...AUDIO_TARGETS, ...VIDEO_TARGETS, ...HTML_TARGETS, ...PDF_TARGETS]

/** Whether a quality setting changes a file of this type. */
export function isLossyTarget(ext: string): boolean {
	return ALL_TARGETS.some((target) => target.ext === ext && target.lossy)
}

// The full set of formats the converter understands, grouped by discipline.
// Ported from Krackn's FormatCatalog.swift. Coverage comes from routing to the
// right backend rather than implementing codecs.

export type ConversionCategory = 'image' | 'document' | 'audio' | 'video' | 'model' | 'archive'

// macOS's `sips` handled these in Krackn; on Windows the common ones go to
// sharp and the rest to ImageMagick (see backends.ts).
export const BASIC_IMAGES = ['jpeg', 'jpg', 'png', 'tiff', 'tif', 'gif', 'bmp', 'heic', 'heif', 'jp2', 'tga', 'psd', 'pict', 'sgi', 'exr'] as const

export const MAGICK_IMAGES = [
	'webp', 'avif', 'svg', 'ico', 'pcx', 'ppm', 'pgm', 'pbm', 'pnm', 'xpm',
	'dds', 'hdr', 'jxl', 'dpx', 'fits', 'miff', 'pfm', 'rgb', 'xbm',
	// camera RAW
	'cr2', 'cr3', 'nef', 'arw', 'dng', 'orf', 'raf', 'rw2', 'pef', 'srw',
] as const

export const AUDIO = [
	'mp3', 'm4a', 'aac', 'wav', 'flac', 'ogg', 'opus', 'aiff', 'aif',
	'wma', 'alac', 'amr', 'ac3', 'dts', 'caf', 'au', 'mka', 'spx', 'ape',
	'wv', 'mp2', 'ra', 'voc', 'w64',
] as const

export const VIDEO = [
	'mp4', 'mov', 'mkv', 'avi', 'webm', 'flv', 'wmv', 'm4v', 'mpg', 'mpeg',
	'mts', 'm2ts', 'ts', 'vob', 'ogv', '3gp', '3g2', 'asf', 'rm', 'rmvb',
	'divx', 'f4v', 'mxf', 'y4m', 'gif', 'apng', 'prores',
] as const

// Pandoc handles the long tail of markup and publishing formats.
export const DOCUMENTS = [
	'pdf', 'docx', 'doc', 'odt', 'rtf', 'rtfd', 'txt', 'md', 'markdown',
	'html', 'htm', 'xhtml', 'epub', 'tex', 'latex', 'org', 'rst',
	'wiki', 'textile', 'asciidoc', 'adoc', 'opml', 'fb2', 'icml',
	'docbook', 'jats', 'man', 'ms', 'csv', 'tsv', 'json', 'yaml', 'xml',
	'pptx', 'key', 'pages', 'numbers', 'xlsx', 'ods', 'webarchive',
] as const

// meshio covers simulation/FEA meshes: the STL <-> MSH path.
export const MESHES = [
	'stl', 'msh', 'vtk', 'vtu', 'vtp', 'xdmf', 'xmf', 'obj', 'off', 'ply',
	'mesh', 'meshb', 'med', 'bdf', 'fem', 'nas', 'inp', 'cgns', 'e', 'exo',
	'ugrid', 'avs', 'dato', 'h5m', 'post', 'su2', 'svg3d', 'tec', 'wkt',
	'gmsh', 'abaqus', 'ansys', 'dolfin', 'permas', 'flac3d', 'mdpa', 'neu',
] as const

// Design and 3D-printing interchange. Handled by meshio where possible; a few
// are read-only in practice.
export const MODEL_INTERCHANGE = [
	'glb', 'gltf', 'fbx', 'dae', '3ds', '3mf', 'amf', 'x3d', 'usdz', 'usd',
	'step', 'stp', 'iges', 'igs', 'brep', 'wrl', 'vrml',
] as const

export const ARCHIVES = ['zip', 'tar', 'gz', 'bz2', 'xz', '7z', 'rar', 'zst'] as const

const has = (list: readonly string[], ext: string) => list.includes(ext)

export function allInputs(): Set<string> {
	return new Set<string>([...BASIC_IMAGES, ...MAGICK_IMAGES, ...AUDIO, ...VIDEO, ...DOCUMENTS, ...MESHES, ...MODEL_INTERCHANGE, ...ARCHIVES])
}

export function category(ext: string): ConversionCategory | undefined {
	const e = ext.toLowerCase().replace(/^\./, '')
	// Order matters: several extensions appear in more than one list.
	if (has(MESHES, e) || has(MODEL_INTERCHANGE, e)) return 'model'
	if (has(VIDEO, e)) return 'video'
	if (has(AUDIO, e)) return 'audio'
	if (has(BASIC_IMAGES, e) || has(MAGICK_IMAGES, e)) return 'image'
	if (has(DOCUMENTS, e)) return 'document'
	if (has(ARCHIVES, e)) return 'archive'
	return undefined
}

const LABELS: Record<string, string> = {
	jpeg: 'JPEG', png: 'PNG', tiff: 'TIFF', heic: 'HEIC', webp: 'WebP', avif: 'AVIF', svg: 'SVG (vector)', jxl: 'JPEG XL',
	mp4: 'MP4 (H.264)', webm: 'WebM (VP9)', mkv: 'Matroska', gif: 'Animated GIF', prores: 'ProRes (.mov)',
	mp3: 'MP3', m4a: 'M4A / AAC', wav: 'WAV (lossless)', flac: 'FLAC (lossless)', opus: 'Opus',
	pdf: 'PDF', docx: 'Word (.docx)', epub: 'ePub', tex: 'LaTeX', md: 'Markdown', rst: 'reStructuredText',
	stl: 'STL (3D print)', msh: 'Gmsh MSH (FEA)', vtk: 'VTK (legacy)', vtu: 'VTU (XML)', obj: 'OBJ', ply: 'PLY', off: 'OFF',
	xdmf: 'XDMF', inp: 'Abaqus INP', bdf: 'Nastran BDF', med: 'MED (Salome)', cgns: 'CGNS',
	glb: 'glTF Binary', gltf: 'glTF', usdz: 'USDZ', '3mf': '3MF',
}

export function label(ext: string): string {
	return LABELS[ext] ?? ext.toUpperCase()
}

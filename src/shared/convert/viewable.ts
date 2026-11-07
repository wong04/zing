// What the file viewer does with each extension. The C# host reads the same lists (generated into
// host/Zing.Core/Defaults/viewable.json), so the renderer's answer and the host's can't differ.
//
// Nothing here is shown as a web page: HTML, SVG and XML would run script in the viewer's origin, so they are not listed.
// A format is listed only if a decoder can read it without reaching a file or the network (no playlists, no MSL/MVG).

export type NativeShow = 'image' | 'video' | 'audio' | 'pdf'

export interface NativeType {
	readonly show: NativeShow
	/** Chosen from the extension, and sent with `nosniff`: the viewer never lets the browser guess it from the file. */
	readonly contentType: string
}

/** Formats Chromium shows without help. */
export const nativeTypes: Readonly<Record<string, NativeType>> = {
	jpg: { show: 'image', contentType: 'image/jpeg' },
	jpeg: { show: 'image', contentType: 'image/jpeg' },
	png: { show: 'image', contentType: 'image/png' },
	gif: { show: 'image', contentType: 'image/gif' },
	webp: { show: 'image', contentType: 'image/webp' },
	bmp: { show: 'image', contentType: 'image/bmp' },
	ico: { show: 'image', contentType: 'image/x-icon' },
	avif: { show: 'image', contentType: 'image/avif' },
	mp4: { show: 'video', contentType: 'video/mp4' },
	m4v: { show: 'video', contentType: 'video/mp4' },
	webm: { show: 'video', contentType: 'video/webm' },
	ogv: { show: 'video', contentType: 'video/ogg' },
	mp3: { show: 'audio', contentType: 'audio/mpeg' },
	wav: { show: 'audio', contentType: 'audio/wav' },
	ogg: { show: 'audio', contentType: 'audio/ogg' },
	oga: { show: 'audio', contentType: 'audio/ogg' },
	opus: { show: 'audio', contentType: 'audio/ogg' },
	flac: { show: 'audio', contentType: 'audio/flac' },
	m4a: { show: 'audio', contentType: 'audio/mp4' },
	aac: { show: 'audio', contentType: 'audio/aac' },
	pdf: { show: 'pdf', contentType: 'application/pdf' },
}

/** Formats a converter reads: ImageMagick for pictures, FFmpeg for video and sound. */
export const viewableLists = {
	image: ['heic', 'heif', 'tif', 'tiff', 'jp2', 'tga', 'psd', 'pcx', 'ppm', 'pgm', 'pbm', 'pnm', 'dds', 'hdr', 'jxl', 'cr2', 'nef', 'arw', 'dng', 'orf', 'raf', 'rw2', 'pef', 'srw'],
	video: ['mov', 'mkv', 'avi', 'flv', 'wmv', 'mpg', 'mpeg', 'mts', 'm2ts', 'vob', '3gp', '3g2', 'asf', 'f4v', 'mxf', 'divx', 'rm', 'rmvb'],
	audio: ['wma', 'aiff', 'aif', 'alac', 'amr', 'ac3', 'dts', 'caf', 'au', 'mka', 'spx', 'ape', 'wv', 'mp2', 'voc', 'w64', 'ra'],
} as const

export type ViewPlan =
	| ({ readonly kind: 'native' } & NativeType)
	| { readonly kind: 'image' | 'video' | 'audio' }
	| { readonly kind: 'unsupported' }

const plain = (ext: string): string => ext.toLowerCase().replace(/^\./, '')

/** How to show a file of this extension: as it is, after a conversion, or not at all. */
export function viewPlan(ext: string): ViewPlan {
	const name = plain(ext)
	const native = Object.hasOwn(nativeTypes, name) ? nativeTypes[name] : undefined
	if (native) return { kind: 'native', ...native }
	for (const kind of ['image', 'video', 'audio'] as const) {
		if ((viewableLists[kind] as readonly string[]).includes(name)) return { kind }
	}
	return { kind: 'unsupported' }
}

/** The extension of a file name, without its dot; empty when there is none. */
export function extensionOf(name: string): string {
	const dot = name.lastIndexOf('.')
	return dot < 0 ? '' : name.slice(dot + 1).toLowerCase()
}

/** The kinds of file a tool can ask a read session for (SessionKinds.cs). */
export type SessionKindName = 'image' | 'pdf'

/** What `viewer:open` answers with. */
export interface ViewerOpened {
	/** Names this open file to `viewer:close`. */
	readonly id: string
	/** The file's name, for showing. */
	readonly name: string
	readonly show: NativeShow
	/**
	 * Where it is served from (https://view.zing/<token>): only this window's Zing views can fetch it, only while it is open.
	 * For a conversion that is not `stream`, the address answers 404 until the job's `engines:exit` says it worked.
	 */
	readonly url: string
	/** The engine job making a converted copy; its lines and exit arrive as `engines:output` and `engines:exit`. Absent for a native file. */
	readonly jobId?: string
	/**
	 * True for a converted video: the address serves fragmented MP4 that grows while FFmpeg works (a `<video src>` can't play
	 * that), so the page feeds a MediaSource by `Range` requests. It answers 204 while no new bytes are ready and 416 at the true end.
	 */
	readonly stream: boolean
}

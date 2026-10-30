// Port of the pure parts of Krackn Tests/drop. Krackn's suite drove real
// NSItemProviders and the network; here the same cases go through the
// planning and naming the main process uses. Skipped as AppKit-only: the two
// `UTType.item` registration checks and "downloaded non-empty" (network).
import { describe, expect, it } from 'vitest'
import { section, Section, tool, UtilityTool } from '../toolbox/panelContent'
import {
	acceptedDragTypes,
	acceptsDrag,
	acceptsFiles,
	asFilePath,
	asWebURL,
	detectExtension,
	hoverDragTypes,
	nameForDownload,
	nameForDroppedImage,
	noticesDrag,
	pathExtension,
	planDrop,
	safeFileName,
	uniqueName,
	type DropPlan,
} from './dropRouter'

const now = new Date('2025-07-29T14:40:00Z')
const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, ...new Array<number>(40).fill(0)])

function basename(path: string): string {
	return path.split(/[\\/]/).pop() ?? ''
}

function openedPath(plan: DropPlan | null): string | null {
	return plan?.kind === 'open' ? plan.path : null
}

describe('Krackn Tests/drop', () => {
	it('file URL drag', () => {
		const plan = planDrop({ fileLocation: 'file:///C:/Users/me/AppData/Local/Temp/drop-src.png' }, now)
		expect(basename(openedPath(plan) ?? '')).toBe('drop-src.png')
	})

	describe('raw image bytes (a browser dragging an <img>)', () => {
		const plan = planDrop({ image: png, suggestedName: 'from-web' }, now)
		it('raw image bytes', () => {
			expect(plan?.kind).toBe('write')
		})
		it('extension sniffed', () => {
			expect(plan?.kind === 'write' && pathExtension(plan.name)).toBe('png')
		})
		it('bytes intact', () => {
			expect(plan?.kind === 'write' && plan.bytes).toEqual(png)
		})
	})

	it('web URL downloaded (planned; the fetch itself is the main process)', () => {
		const plan = planDrop({ url: 'https://www.google.com/favicon.ico' }, now)
		expect(plan).toEqual({ kind: 'download', url: 'https://www.google.com/favicon.ico' })
		expect(nameForDownload('https://www.google.com/favicon.ico', 'image/x-icon', new Uint8Array(200))).toBe('favicon.ico')
	})

	it('junk rejected', () => {
		expect(planDrop({ url: 'not a url at all' }, now)).toBeNull()
		expect(planDrop({ otherTypes: ['text/plain'] }, now)).toBeNull()
	})

	// Dragging *between* the app's own tools, for every kind of file. In
	// Krackn only images and PDFs worked until files were offered as file
	// URLs; everything else was refused before it reached the router.
	describe('every kind of file survives a tool-to-tool drag', () => {
		const exotic = ['stl', 'msh', 'vtu', 'mp4', 'mkv', 'flac', 'zip', 'docx', 'pdf', 'png', 'obj', 'heic', 'svg', 'bin']
		const handled = exotic.filter(
			(ext) => pathExtension(openedPath(planDrop({ fileLocation: `C:\\Temp\\zing-drag-test\\drag-test.${ext}` }, now)) ?? '') === ext,
		)
		it.each(exotic)('.%s survives a tool-to-tool drag', (ext) => {
			expect(handled).toContain(ext)
		})
		it('every file type is draggable', () => {
			expect(handled).toHaveLength(exotic.length)
		})
	})
})

// Zing additions.
describe('planning', () => {
	it('a file location wins over everything else on offer', () => {
		const plan = planDrop({ fileLocation: 'C:\\a.png', image: png, url: 'https://x.com/a.png' }, now)
		expect(plan).toEqual({ kind: 'open', path: 'C:\\a.png' })
	})
	it('empty image bytes fall through to the URL', () => {
		expect(planDrop({ image: new Uint8Array(), url: 'https://x.com/a.png' }, now)?.kind).toBe('download')
	})
	it('other representations are materialised, text ones never', () => {
		expect(planDrop({ otherTypes: ['text/plain', 'model/stl', 'text/uri-list', 'Files'] }, now)).toEqual({
			kind: 'materialise',
			types: ['model/stl', 'Files'],
		})
	})
	it('unnamed bytes get a timestamped name and a sniffed extension', () => {
		expect(nameForDroppedImage(png, undefined, now)).toBe('dropped-1753800000.png')
		expect(nameForDroppedImage(new Uint8Array(8), 'x', now)).toBe('x.png')
		expect(nameForDroppedImage(png, 'photo.jpeg', now)).toBe('photo.jpeg')
	})
})

describe('locations and URLs', () => {
	it('file URLs become Windows paths', () => {
		expect(asFilePath('file:///C:/Users/me/My%20Pictures/a.png')).toBe('C:\\Users\\me\\My Pictures\\a.png')
		expect(asFilePath('file://server/share/a.png')).toBe('\\\\server\\share\\a.png')
		expect(asFilePath('D:\\x.stl')).toBe('D:\\x.stl')
		expect(asFilePath('https://x.com/a.png')).toBeNull()
		expect(asFilePath('a.png')).toBeNull()
	})
	it('only http(s) is downloaded', () => {
		expect(asWebURL('  https://x.com/a b.png \n')).toBe('https://x.com/a%20b.png')
		expect(asWebURL('javascript:alert(1)')).toBeNull()
		expect(asWebURL('file:///C:/a.png')).toBeNull()
		expect(asWebURL('ftp://x.com/a')).toBeNull()
	})
})

describe('naming downloads', () => {
	it('a bare host is "download", typed from the response', () => {
		expect(nameForDownload('https://x.com/', 'image/jpeg', new Uint8Array())).toBe('download.jpeg')
	})
	it('the query never reaches the name', () => {
		expect(nameForDownload('https://x.com/img?id=7', null, png)).toBe('img.png')
	})
	it('unknown everything is .bin', () => {
		expect(nameForDownload('https://x.com/blob', 'application/octet-stream', new Uint8Array(4))).toBe('blob.bin')
	})
	it('percent-escapes are decoded, malformed ones kept literally', () => {
		expect(nameForDownload('https://x.com/my%20cat.png', null, png)).toBe('my cat.png')
		expect(nameForDownload('https://x.com/100%25.png', null, png)).toBe('100%.png')
	})
	it("characters Windows refuses don't reach the disk", () => {
		expect(nameForDownload('https://x.com/a:b%3F.png', null, png)).toBe('a-b-.png')
		expect(safeFileName('name. . ')).toBe('name')
	})
})

describe('sniffing', () => {
	const bytes = (...values: number[]): Uint8Array => new Uint8Array([...values, ...new Array<number>(12).fill(0)])
	it.each([
		['png', bytes(0x89, 0x50, 0x4e, 0x47)],
		['jpg', bytes(0xff, 0xd8, 0xff, 0xe0)],
		['gif', bytes(0x47, 0x49, 0x46, 0x38)],
		['pdf', bytes(0x25, 0x50, 0x44, 0x46)],
		['webp', bytes(0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50)],
		['mp4', bytes(0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70)],
	])('%s', (want, data) => {
		expect(detectExtension(data)).toBe(want)
	})
	it('too short or unknown is null', () => {
		expect(detectExtension(new Uint8Array([0x89, 0x50, 0x4e]))).toBeNull()
		expect(detectExtension(new Uint8Array(16))).toBeNull()
	})
})

describe('unique names', () => {
	it('counts up from 2, before the extension', () => {
		const taken = new Set(['a.png', 'a 2.png'])
		expect(uniqueName('a.png', (n) => taken.has(n))).toBe('a 3.png')
		expect(uniqueName('README', (n) => n === 'README')).toBe('README 2')
		expect(uniqueName('free.txt', () => false)).toBe('free.txt')
	})
	it('a leading dot is not an extension', () => {
		expect(pathExtension('.hidden')).toBe('')
		expect(pathExtension('archive.tar.gz')).toBe('gz')
	})
})

describe('targets', () => {
	it('Files, Convert and the background remover take dropped files; nothing else does', () => {
		expect(acceptsFiles(section(Section.viewer))).toBe(true)
		expect(acceptsFiles(section(Section.convert))).toBe(true)
		expect(acceptsFiles(tool(UtilityTool.bgRemove))).toBe(true)
		expect(acceptsFiles(section(Section.download))).toBe(false)
		expect(acceptsFiles(tool(UtilityTool.hash))).toBe(false)
	})
	it('hover targets stay narrower than drop targets', () => {
		expect(hoverDragTypes.every((type) => acceptedDragTypes.includes(type))).toBe(true)
		expect(noticesDrag(['text/plain'])).toBe(false)
		expect(acceptsDrag(['text/plain'])).toBe(true)
		expect(noticesDrag(['Files'])).toBe(true)
		expect(acceptsDrag(['text/html'])).toBe(false)
	})
})

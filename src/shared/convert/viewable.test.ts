import { describe, expect, it } from 'vitest'
import { nativeTypes, viewableLists, viewPlan } from './viewable'

describe('viewPlan', () => {
	it('sends formats Chromium cannot show to a converter', () => {
		expect(viewPlan('heic').kind).toBe('image')
		expect(viewPlan('cr2').kind).toBe('image')
		expect(viewPlan('mov').kind).toBe('video')
		expect(viewPlan('mkv').kind).toBe('video')
		expect(viewPlan('wma').kind).toBe('audio')
	})
	it('shows formats Chromium can show as they are', () => {
		expect(viewPlan('pdf')).toMatchObject({ kind: 'native', show: 'pdf' })
		expect(viewPlan('mp4')).toMatchObject({ kind: 'native', show: 'video' })
		expect(viewPlan('png')).toMatchObject({ kind: 'native', show: 'image' })
		expect(viewPlan('mp3')).toMatchObject({ kind: 'native', show: 'audio' })
	})
	it('has nothing to show for a model or an unknown format', () => {
		expect(viewPlan('obj').kind).toBe('unsupported')
		expect(viewPlan('zzz').kind).toBe('unsupported')
		expect(viewPlan('').kind).toBe('unsupported')
	})
	it('ignores case and a leading dot', () => {
		expect(viewPlan('.HEIC').kind).toBe('image')
		expect(viewPlan('Mp4').kind).toBe('native')
	})
	it('never shows a page or a script as it is, whatever its name', () => {
		for (const ext of ['html', 'htm', 'xhtml', 'svg', 'svgz', 'xml', 'js', 'mjs', 'txt', 'json', 'swf']) {
			expect(viewPlan(ext).kind, ext).toBe('unsupported')
		}
	})
	it('never hands a playlist or a script to FFmpeg or ImageMagick', () => {
		for (const ext of ['m3u8', 'm3u', 'ffconcat', 'txt', 'pls', 'msl', 'mvg', 'svg', 'eps', 'ps', 'pdf']) {
			expect(viewPlan(ext).kind === 'video' || viewPlan(ext).kind === 'audio' || viewPlan(ext).kind === 'image', ext).toBe(false)
		}
	})
	it('lists every format once', () => {
		const all = [...Object.keys(nativeTypes), ...viewableLists.image, ...viewableLists.video, ...viewableLists.audio]
		expect(all.length).toBe(new Set(all).size)
	})
	it('gives every native format a content type chosen here, never sniffed', () => {
		for (const [ext, entry] of Object.entries(nativeTypes)) expect(entry.contentType, ext).toMatch(/^(image|video|audio)\/[a-z0-9.+-]+$|^application\/pdf$/)
	})
})

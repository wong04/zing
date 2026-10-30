// Port of Krackn `Tests/unit` "downloads — save what can't be shown, or what's marked as a file".
// (`Tests/download` drives ConvertModel's file saving, not this policy, and isn't pure.)
import { describe, expect, it } from 'vitest'
import { shouldDownload } from './downloadPolicy'

describe('downloads — save what can\'t be shown, or what\'s marked as a file', () => {
	it('a zip opened as a page downloads', () => {
		expect(shouldDownload({ canShow: false, contentDisposition: null, isMainFrame: true })).toBe(true)
	})
	it('Content-Disposition: attachment downloads, even if showable', () => {
		expect(
			shouldDownload({ canShow: true, contentDisposition: 'attachment; filename="a.pdf"', isMainFrame: true }),
		).toBe(true)
	})
	it('…and from a hidden frame (how download buttons often work)', () => {
		expect(shouldDownload({ canShow: true, contentDisposition: ' Attachment ', isMainFrame: false })).toBe(true)
	})
	it('an ordinary page is shown', () => {
		expect(shouldDownload({ canShow: true, contentDisposition: null, isMainFrame: true })).toBe(false)
	})
	it('inline disposition is shown', () => {
		expect(shouldDownload({ canShow: true, contentDisposition: 'inline', isMainFrame: true })).toBe(false)
	})
	it('an odd type in an embedded frame doesn\'t drop a file', () => {
		expect(shouldDownload({ canShow: false, contentDisposition: null, isMainFrame: false })).toBe(false)
	})
})

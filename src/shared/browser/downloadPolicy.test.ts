// Port of Krackn `Tests/unit` "downloads — save what can't be shown, or what's marked as a file".
// (`Tests/download` drives ConvertModel's file saving, not this policy, and isn't pure.)
// The cases live in downloadPolicy.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { shouldDownload } from './downloadPolicy'
import vectors from './downloadPolicy.vectors.json'

describe('downloads — save what can\'t be shown, or what\'s marked as a file', () => {
	it.each(vectors.shouldDownload)('$name', ({ canShow, contentDisposition, isMainFrame, expected }) => {
		expect(shouldDownload({ canShow, contentDisposition, isMainFrame })).toBe(expected)
	})
})

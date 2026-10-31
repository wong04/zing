// Port of Krackn `Tests/unit` "address input — go there, or search for it".
// The cases live in addressInput.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { classify } from './addressInput'
import vectors from './addressInput.vectors.json'

describe('address input — go there, or search for it', () => {
	it.each(vectors.urls)('"%s" opens %s', (text, want) => {
		expect(classify(text)).toEqual({ kind: 'url', url: want })
	})

	it.each(vectors.searches)('"%s" is searched', (text) => {
		expect(classify(text)).toEqual({ kind: 'search', text })
	})

	it.each(vectors.blank)('blank input "%s" does nothing', (text) => {
		expect(classify(text)).toBeNull()
	})
})

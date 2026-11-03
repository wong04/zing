import { describe, expect, it } from 'vitest'
import { knownRecents } from './panelContent'

describe('knownRecents', () => {
	it('keeps ids that name a tool or section and drops the rest, in order', () => {
		expect(knownRecents(['tool:Hashes', 'tool:Gone', 'nonsense', 'tool:JSON'])).toEqual(['tool:Hashes', 'tool:JSON'])
	})
})

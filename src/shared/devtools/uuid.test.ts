// Krackn has no UUID-tool test (its "UUIDs regenerate" check is about DoH
// profiles); these are Zing's.
import { describe, expect, it } from 'vitest'
import { makeUUID, makeUUIDs } from './uuid'

const v4 = /^[0-9A-F]{8}-[0-9A-F]{4}-4[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$/

describe('uuid', () => {
	it('is an upper-case version-4 UUID, like Foundation', () => {
		expect(makeUUID()).toMatch(v4)
	})
	it('"Generate 5" gives five distinct ones', () => {
		const five = makeUUIDs(5)
		expect(five).toHaveLength(5)
		expect(new Set(five).size).toBe(5)
		for (const id of five) expect(id).toMatch(v4)
	})
})

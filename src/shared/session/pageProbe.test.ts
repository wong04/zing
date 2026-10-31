// The cases live in pageProbe.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { PageProbeError, parsePageProbe } from './pageProbe'
import vectors from './pageProbe.vectors.json'

describe('parsePageProbe', () => {
	it.each(vectors.accepted)('$name', ({ input, expect: want }) => {
		expect(parsePageProbe(input)).toEqual(want)
	})
	it.each(vectors.rejected as unknown[])('rejects %j', (value) => {
		expect(() => parsePageProbe(value)).toThrow(PageProbeError)
	})
})

// The cases live in trackerRules.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { TRACKER_DOMAINS, blocksTrackerRequest, isTrackerHost } from './trackerRules'
import vectors from './trackerRules.vectors.json'

describe('tracker rules', () => {
	it("keeps Krackn's list, each domain once", () => {
		expect(TRACKER_DOMAINS).toHaveLength(vectors.domainCount)
		expect(new Set(TRACKER_DOMAINS).size).toBe(TRACKER_DOMAINS.length)
	})
	it.each(vectors.hosts)('$name', ({ host, tracker }) => {
		expect(isTrackerHost(host)).toBe(tracker)
	})
	it.each(vectors.requests)('$name', ({ request, page, blocked }) => {
		expect(blocksTrackerRequest(request, page)).toBe(blocked)
	})
})

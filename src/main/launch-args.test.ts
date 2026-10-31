import { describe, expect, it } from 'vitest'
import { LinkInbox, parseLaunchUrls } from './launch-args'
import vectors from './launch-args.vectors.json'

// Shared with host/Zing.Core.Tests/LaunchArgsTests.cs.
describe('parseLaunchUrls', () => {
	it.each(vectors)('$name', ({ argv, urls }) => {
		expect(parseLaunchUrls(argv)).toEqual(urls)
	})
})

describe('LinkInbox', () => {
	it('queues links until a sink attaches, then delivers them in order', () => {
		const inbox = new LinkInbox()
		inbox.push('https://a.test')
		inbox.push('https://b.test')
		expect(inbox.pendingCount).toBe(2)
		const seen: string[] = []
		inbox.attach((u) => seen.push(u))
		expect(seen).toEqual(['https://a.test', 'https://b.test'])
		expect(inbox.pendingCount).toBe(0)
	})

	it('delivers later links straight away', () => {
		const inbox = new LinkInbox()
		const seen: string[] = []
		inbox.attach((u) => seen.push(u))
		inbox.push('https://c.test')
		expect(seen).toEqual(['https://c.test'])
	})
})

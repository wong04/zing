import { describe, expect, it } from 'vitest'
import { LinkInbox, parseLaunchUrls } from './launch-args'

describe('parseLaunchUrls', () => {
	it('finds http and https links and skips the executable, app path and switches', () => {
		const argv = ['C:\\Program Files\\Zing\\Zing.exe', '.', '--user-data-dir=C:\\tmp', 'https://example.com/a?b=1', 'http://x.test']
		expect(parseLaunchUrls(argv)).toEqual(['https://example.com/a?b=1', 'http://x.test'])
	})

	it('removes the quotes Windows leaves around an argument', () => {
		expect(parseLaunchUrls(['"https://example.com/?a=1&b=2"'])).toEqual(['https://example.com/?a=1&b=2'])
		expect(parseLaunchUrls(["'https://example.com'"])).toEqual(['https://example.com'])
		expect(parseLaunchUrls(['https://example.com/path/"'])).toEqual(['https://example.com/path/'])
		expect(parseLaunchUrls(['  "https://example.com"  '])).toEqual(['https://example.com'])
	})

	it('refuses schemes a link click should never open', () => {
		const argv = ['file:///C:/secret.txt', 'javascript:alert(1)', 'ms-settings:defaultapps', 'C:\\Users\\me\\page.html']
		expect(parseLaunchUrls(argv)).toEqual([])
	})

	it('does not read a URL out of a switch value', () => {
		expect(parseLaunchUrls(['--url=https://example.com', '--app=https://example.com'])).toEqual([])
	})

	it('returns nothing for an empty or link-less command line', () => {
		expect(parseLaunchUrls([])).toEqual([])
		expect(parseLaunchUrls(['Zing.exe', '', '""'])).toEqual([])
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

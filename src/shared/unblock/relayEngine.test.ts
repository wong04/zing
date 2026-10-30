// Port of section B of Krackn's Tests/unit/main.swift (14 assertions).

import { describe, expect, it } from 'vitest'
import {
	allRelayStrategies,
	BadURLError,
	EmptyBodyError,
	HTTPStatusError,
	normalize,
	returnsLiveDocument,
	RelayStrategy,
	validateStatus,
	waybackSnapshotURL,
} from './relayEngine'

describe('RelayEngine.normalize', () => {
	// Krackn only checks that these parse; the expected forms are kept for reference.
	it.each([
		['example.com', 'https://example.com'],
		['  example.com  ', 'https://example.com'],
		['<https://a.com/x>', 'https://a.com/x'],
		['http://a.com', 'http://a.com'],
		['https://a.com:8443/p?q=1', 'https://a.com:8443/p?q=1'],
		['a.com/path with space', 'https://a.com/path with space'],
		['HTTPS://UPPER.COM', 'HTTPS://UPPER.COM'],
		['sub.domain.co.uk/x', 'https://sub.domain.co.uk/x'],
	])("normalize '%s'", (input) => {
		expect(normalize(input)).toBeInstanceOf(URL)
	})

	it.each(['', '   ', '<>', '""', "'"])("reject '%s'", (input) => {
		expect(() => normalize(input)).toThrow(BadURLError)
	})

	it('no crash on hostile input', () => {
		// Each may be accepted or refused, but a refusal must be a BadURLError,
		// never a stray TypeError from the URL parser.
		for (const weird of [
			'http://', 'https://', '://x', 'a b c', 'a'.repeat(5000),
			'https://[::1]/', 'https://user:pw@host.com/', 'café.com', 'https://.',
			'javascript:alert(1)', 'file:///etc/passwd', '\n\n\n',
		]) {
			try {
				normalize(weird)
			} catch (error) {
				if (!(error instanceof BadURLError)) throw error
			}
		}
		expect(true).toBe(true)
	})
})

describe('RelayStrategy', () => {
	it('only direct and pinned fetches return a live document', () => {
		expect(allRelayStrategies.filter(returnsLiveDocument)).toEqual([RelayStrategy.direct, RelayStrategy.dohPinned])
	})
})

// Beyond Krackn's suite, which only reaches these through live fetches.
describe('validateStatus', () => {
	it.each([200, 204, 302, 399])('%s passes', (status) => {
		expect(() => validateStatus(status)).not.toThrow()
	})
	it.each([199, 400, 404, 503])('%s throws', (status) => {
		expect(() => validateStatus(status)).toThrow(HTTPStatusError)
	})
})

describe('waybackSnapshotURL', () => {
	it('upgrades the snapshot to https, leaving the archived URL alone', () => {
		const json = { archived_snapshots: { closest: { available: true, url: 'http://web.archive.org/web/2024/http://example.com/' } } }
		expect(waybackSnapshotURL(json).href).toBe('https://web.archive.org/web/2024/http://example.com/')
	})
	it.each([
		['no snapshot', { archived_snapshots: {} }],
		['unavailable', { archived_snapshots: { closest: { available: false, url: 'http://web.archive.org/x' } } }],
		['no url', { archived_snapshots: { closest: { available: true } } }],
		['not an object', 'nope'],
	])('%s is an empty result', (_, json) => {
		expect(() => waybackSnapshotURL(json)).toThrow(EmptyBodyError)
	})
})

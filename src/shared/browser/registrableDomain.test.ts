// Port of Krackn `Tests/taborg` "registrable domain".
import { describe, expect, it } from 'vitest'
import { registrableDomain } from './registrableDomain'

describe('registrable domain', () => {
	it.each([
		['docs.google.com', 'google.com'],
		['google.com', 'google.com'],
		['a.b.c.example.com', 'example.com'],
		// Two-part public suffixes must not collapse to the meaningless "co.uk".
		['news.bbc.co.uk', 'bbc.co.uk'],
		['bbc.co.uk', 'bbc.co.uk'],
		['shop.example.com.au', 'example.com.au'],
		['localhost', 'localhost'],
		// Root domain regardless of how the host is written.
		['MAIL.Google.COM', 'google.com'],
		['www.youtube.com.', 'youtube.com'],
		['m.youtube.com', 'youtube.com'],
		['en.wikipedia.org', 'wikipedia.org'],
		['forums.example.co.jp', 'example.co.jp'],
		['www.example.com.hk', 'example.com.hk'],
		// Addresses are not domains: cutting them would merge different machines.
		['192.168.1.10', '192.168.1.10'],
		['10.0.0.1', '10.0.0.1'],
		['::1', '::1'],
	])('%s -> %s', (host, want) => {
		expect(registrableDomain(host)).toBe(want)
	})
})

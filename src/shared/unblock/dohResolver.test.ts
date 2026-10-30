// Krackn tests DoHResolver only against live resolvers (Tests/network); these
// cover the ported query building and answer parsing offline.

import { describe, expect, it } from 'vitest'
import {
	DoHProvider,
	dohQueryURL,
	dohRequestHeaders,
	filtersMalware,
	MalformedDoHResponseError,
	parseDoHAnswer,
	parseDoHResponse,
} from './dohResolver'
import { NXDomainError, ResolverStatusError, ResolverUnreachableError } from './relayEngine'

describe('dohQueryURL', () => {
	it('asks for A records by name', () => {
		expect(dohQueryURL('example.com', DoHProvider.google).href).toBe('https://dns.google/resolve?name=example.com&type=A')
	})
	it('uses Quad9 on its JSON port', () => {
		expect(dohQueryURL('a.b', DoHProvider.quad9).port).toBe('5053')
	})
})

describe('dohRequestHeaders', () => {
	it('sends the dns-json Accept header to Cloudflare', () => {
		expect(dohRequestHeaders(DoHProvider.cloudflare)).toEqual({ Accept: 'application/dns-json' })
		expect(dohRequestHeaders(DoHProvider.cloudflareSecurity)).toEqual({ Accept: 'application/dns-json' })
	})
	it('sends nothing extra to Google or Quad9', () => {
		expect(dohRequestHeaders(DoHProvider.google)).toEqual({})
		expect(dohRequestHeaders(DoHProvider.quad9)).toEqual({})
	})
})

describe('filtersMalware', () => {
	it('is true only for the filtering resolvers', () => {
		const filtering = Object.values(DoHProvider).filter(filtersMalware)
		expect(filtering).toEqual([DoHProvider.cloudflareSecurity, DoHProvider.quad9])
	})
})

describe('parseDoHAnswer', () => {
	it('keeps A records and drops CNAMEs', () => {
		const json = {
			Status: 0,
			Answer: [
				{ name: 'www.example.com', type: 5, data: 'example.com.' },
				{ name: 'example.com', type: 1, data: '93.184.216.34' },
				{ name: 'example.com', type: 1, data: '93.184.216.35' },
			],
		}
		expect(parseDoHAnswer('www.example.com', json)).toEqual(['93.184.216.34', '93.184.216.35'])
	})
	it('a name with no Answer resolved to nothing', () => {
		expect(parseDoHAnswer('example.com', { Status: 0 })).toEqual([])
	})
	it('status 3 is NXDOMAIN', () => {
		expect(() => parseDoHAnswer('nope.invalid', { Status: 3 })).toThrow(NXDomainError)
	})
	it('any other status is a resolver error', () => {
		expect(() => parseDoHAnswer('example.com', { Status: 2 })).toThrow(ResolverStatusError)
	})
	it('a missing Status is malformed', () => {
		expect(() => parseDoHAnswer('example.com', { Answer: [] })).toThrow(MalformedDoHResponseError)
	})
	it('an answer without data is malformed', () => {
		expect(() => parseDoHAnswer('example.com', { Status: 0, Answer: [{ name: 'x', type: 1 }] })).toThrow(MalformedDoHResponseError)
	})
})

describe('parseDoHResponse', () => {
	it('a non-200 means the resolver is unreachable', () => {
		expect(() => parseDoHResponse('example.com', DoHProvider.google, 400, '')).toThrow(ResolverUnreachableError)
	})
	it('a body that is not JSON is malformed', () => {
		expect(() => parseDoHResponse('example.com', DoHProvider.google, 200, '<html>')).toThrow(MalformedDoHResponseError)
	})
	it('reads a good answer', () => {
		const body = JSON.stringify({ Status: 0, Answer: [{ name: 'a.com', type: 1, data: '1.2.3.4' }] })
		expect(parseDoHResponse('a.com', DoHProvider.google, 200, body)).toEqual(['1.2.3.4'])
	})
})

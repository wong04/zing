// Port of Krackn `Tests/unit` "address input — go there, or search for it".
import { describe, expect, it } from 'vitest'
import { classify } from './addressInput'

describe('address input — go there, or search for it', () => {
	it.each([
		['apple.com', 'https://apple.com'],
		['docs.google.com', 'https://docs.google.com'],
		['apple.com/iphone', 'https://apple.com/iphone'],
		['bbc.co.uk', 'https://bbc.co.uk'],
		['purdue.edu/admissions?x=1', 'https://purdue.edu/admissions?x=1'],
		['github.io', 'https://github.io'],
		['  Apple.com  ', 'https://Apple.com'],
		['https://example.com/a%20b', 'https://example.com/a%20b'],
		['http://neverssl.com', 'http://neverssl.com'],
		['localhost:3000', 'http://localhost:3000'],
		['192.168.1.1', 'http://192.168.1.1'],
		['myserver.lan:8080/status', 'https://myserver.lan:8080/status'],
		['about:blank', 'about:blank'],
	])('"%s" opens %s', (text, want) => {
		expect(classify(text)).toEqual({ kind: 'url', url: want })
	})

	it.each([
		'node.js',
		'index.html',
		'file.txt',
		'what is apple.com',
		'hello',
		'2+2',
		'swift.version',
		'c++ tutorial',
	])('"%s" is searched', (text) => {
		expect(classify(text)).toEqual({ kind: 'search', text })
	})

	it('empty input does nothing', () => {
		expect(classify('   ')).toBeNull()
	})
})

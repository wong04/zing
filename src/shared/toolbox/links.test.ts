import { describe, expect, it } from 'vitest'
import { InvalidLinkError } from '../browser/linkCleaner'
import { DEFAULT_TOOL_LINKS, parseToolLink } from './links'

describe('parseToolLink', () => {
	it('adds https and a path to a bare domain', () => {
		expect(parseToolLink('figma.com')).toBe('https://figma.com/')
	})

	it('keeps an http address as written', () => {
		expect(parseToolLink(' http://127.0.0.1:8080/a?b=1 ')).toBe('http://127.0.0.1:8080/a?b=1')
	})

	it.each(['javascript:alert(1)', 'javascript://x/%0aalert(1)', 'file:///c:/x', 'data:text/html,hi', 'ftp://example.com/', ''])('rejects %j', (input) => {
		expect(() => parseToolLink(input)).toThrow(InvalidLinkError)
	})
})

describe('the default links', () => {
	it('are Desmos, Figma, Notion, ChatGPT, Claude and Gemini, each a link parseToolLink keeps as it is', () => {
		expect(DEFAULT_TOOL_LINKS.map((link) => link.title)).toEqual(['Desmos', 'Figma', 'Notion', 'ChatGPT', 'Claude', 'Gemini'])
		for (const link of DEFAULT_TOOL_LINKS) expect(parseToolLink(link.url)).toBe(link.url)
	})
})

import { readFileSync } from 'node:fs'
import { describe, expect, it, vi } from 'vitest'
import { conventionalIconUrls, FaviconStore } from './favicons'

function store(capacity = 256) {
	const fetchIcon = vi.fn(async (candidates: readonly string[]) => `data:image/png;base64,${candidates[0]}`)
	const onChange = vi.fn()
	return { fetchIcon, onChange, favicons: new FaviconStore(fetchIcon, onChange, capacity) }
}

describe('FaviconStore', () => {
	it('prefers the declared icon over the host icon', async () => {
		const { favicons } = store()
		await favicons.loadHost('docs.google.com')
		await favicons.loadIcon('https://docs.google.com/docs-icon.png')
		expect(favicons.icon('https://docs.google.com/docs-icon.png', 'docs.google.com')).toContain('docs-icon.png')
	})

	it('falls back to the host icon while the declared one has not loaded', async () => {
		const { favicons } = store()
		await favicons.loadHost('example.com')
		expect(favicons.icon('https://example.com/declared.png', 'example.com')).toContain('https://example.com/favicon.ico')
	})

	it('keys by full host, so sibling subdomains never share an icon', async () => {
		const { favicons } = store()
		await favicons.loadHost('mail.google.com')
		expect(favicons.icon(undefined, 'mail.google.com')).toBeDefined()
		expect(favicons.icon(undefined, 'docs.google.com')).toBeUndefined()
		expect(favicons.icon(undefined, 'google.com')).toBeUndefined()
	})

	it('treats host case as the same host', async () => {
		const { favicons } = store()
		await favicons.loadHost('Example.COM')
		expect(favicons.icon(undefined, 'example.com')).toBeDefined()
	})

	it('fetches each key once, even for concurrent requests', async () => {
		const { favicons, fetchIcon } = store()
		await Promise.all([favicons.loadIcon('https://a.test/i.png'), favicons.loadIcon('https://a.test/i.png')])
		await favicons.loadIcon('https://a.test/i.png')
		expect(fetchIcon).toHaveBeenCalledTimes(1)
	})

	it('remembers a miss instead of fetching again', async () => {
		const fetchIcon = vi.fn(async () => null)
		const favicons = new FaviconStore(fetchIcon, () => undefined)
		await favicons.loadHost('none.test')
		await favicons.loadHost('none.test')
		expect(fetchIcon).toHaveBeenCalledTimes(1)
		expect(favicons.icon(undefined, 'none.test')).toBeUndefined()
	})

	it('asks the host for its conventional icon locations', async () => {
		const { favicons, fetchIcon } = store()
		await favicons.loadHost('example.com')
		expect(fetchIcon).toHaveBeenCalledWith(conventionalIconUrls('example.com'))
	})

	it('evicts the least recently used entry at capacity', async () => {
		const { favicons } = store(2)
		await favicons.loadIcon('https://a.test/1.png')
		await favicons.loadIcon('https://a.test/2.png')
		favicons.icon('https://a.test/1.png', '')
		await favicons.loadIcon('https://a.test/3.png')
		expect(favicons.size).toBe(2)
		expect(favicons.icon('https://a.test/2.png', '')).toBeUndefined()
		expect(favicons.icon('https://a.test/1.png', '')).toBeDefined()
		expect(favicons.icon('https://a.test/3.png', '')).toBeDefined()
	})

	it('reports a change after each load', async () => {
		const { favicons, onChange } = store()
		await favicons.loadIcon('https://a.test/1.png')
		expect(onChange).toHaveBeenCalledTimes(1)
	})

	it('has no way to write to disk', () => {
		const source = readFileSync(new URL('./favicons.ts', import.meta.url), 'utf8')
		expect(source).not.toMatch(/from 'node:fs|from 'fs|localStorage|writeFile/)
	})
})

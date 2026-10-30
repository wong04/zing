import { describe, expect, it, vi } from 'vitest'
import { SettingsStore, parseSettings } from '@shared/settings'

vi.mock('electron', () => ({ protocol: {} }))

const { classifyNavigation, isStartPage, StartPage, startPageTheme } = await import('./start-page')

describe('isStartPage', () => {
	it('recognises the home page with or without a slash', () => {
		expect(isStartPage('zing://home')).toBe(true)
		expect(isStartPage('zing://home/')).toBe(true)
	})
	it('rejects other hosts and schemes', () => {
		expect(isStartPage('zing://edit-shortcuts')).toBe(false)
		expect(isStartPage('https://home')).toBe(false)
		expect(isStartPage('not a url')).toBe(false)
	})
})

describe('classifyNavigation', () => {
	it('opens Settings only from the start page itself', () => {
		expect(classifyNavigation('zing://home', 'zing://edit-shortcuts', 'google')).toEqual({ kind: 'edit-shortcuts' })
		expect(classifyNavigation('https://evil.test/', 'zing://edit-shortcuts', 'google')).toEqual({ kind: 'block' })
	})
	it('treats the form submission as a query for the address rule', () => {
		expect(classifyNavigation('zing://home', 'https://www.google.com/search?q=apple.com', 'google')).toEqual({ kind: 'search', query: 'apple.com' })
		expect(classifyNavigation('zing://home', 'https://duckduckgo.com/?q=two+words', 'duckduckgo')).toEqual({ kind: 'search', query: 'two words' })
	})
	it('leaves tiles alone', () => {
		expect(classifyNavigation('zing://home', 'https://github.com/', 'google')).toEqual({ kind: 'allow' })
	})
	it('does not intercept the same URL when it comes from a website', () => {
		expect(classifyNavigation('https://example.com/', 'https://www.google.com/search?q=x', 'google')).toEqual({ kind: 'allow' })
	})
	it('only intercepts the engine that is currently chosen', () => {
		expect(classifyNavigation('zing://home', 'https://www.bing.com/search?q=x', 'google')).toEqual({ kind: 'allow' })
	})
})

describe('StartPage.html', () => {
	const page = (stored: object = {}) => new StartPage(new SettingsStore(parseSettings(stored))).html()

	it('shows the default tiles until shortcuts are edited', () => {
		expect(page().match(/class="tile"/g)).toHaveLength(8)
	})
	it('shows the edited shortcuts', () => {
		const html = page({ startShortcuts: [{ title: 'Mine', url: 'https://mine.test' }] })
		expect(html.match(/class="tile"/g)).toHaveLength(1)
		expect(html).toContain('Mine')
	})
	it('submits to the chosen search engine', () => {
		expect(page({ searchEngine: 'duckduckgo' })).toContain('<form action="https://duckduckgo.com/"')
	})
	it('paints with the chosen accent', () => {
		expect(page({ accent: 'blue' })).toContain(startPageTheme('blue').light.accent)
		expect(startPageTheme('blue').light.accent).not.toBe(startPageTheme('yellow').light.accent)
	})
})

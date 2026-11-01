import { describe, expect, it } from 'vitest'
import type { TabView } from '@shared/ipc'
import { pageFaultText, tabTooltip } from './display'

function tab(patch: Partial<TabView> = {}): TabView {
	return {
		id: 't1',
		url: 'https://example.com/',
		title: 'Example',
		pinned: false,
		createdAt: 0,
		lastActiveAt: 0,
		updatedAt: 0,
		loading: false,
		canGoBack: false,
		canGoForward: false,
		...patch,
	}
}

describe('a tab whose page is not working', () => {
	it('says nothing while the page works', () => {
		expect(pageFaultText(tab())).toBeUndefined()
		expect(pageFaultText(undefined)).toBeUndefined()
		expect(tabTooltip(tab())).toBe('Example')
	})

	it('names a crash and a hang', () => {
		expect(pageFaultText(tab({ pageState: 'crashed' }))).toBe('This page crashed')
		expect(pageFaultText(tab({ pageState: 'unresponsive' }))).toBe('This page isn’t responding')
	})

	it('adds the fault to the chip tooltip', () => {
		expect(tabTooltip(tab({ pageState: 'crashed' }))).toBe('Example (crashed)')
		expect(tabTooltip(tab({ pageState: 'unresponsive' }))).toBe('Example (not responding)')
	})
})

import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { parsePort, TOKEN_PLACEHOLDER, withToken } from '../settings/AiControlSection'
import { AiConfirmBar } from './AiBars'
import { aiTabClasses } from './useAiTabs'

vi.mock('../api', () => ({ zing: {} }))

describe('the confirm bar', () => {
	it('draws the page-influenced description as text, never markup', () => {
		const html = renderToStaticMarkup(<AiConfirmBar confirm={{ tab: 't', description: 'click <img src=x onerror=alert(1)>', canAlwaysAllow: true }} />)
		expect(html).toContain('AI wants to click &lt;img src=x onerror=alert(1)&gt;')
		expect(html).not.toContain('<img')
		expect(html).toContain('Always allow on this site')
	})

	it('has no Always allow where the address has no site', () => {
		const html = renderToStaticMarkup(<AiConfirmBar confirm={{ tab: 't', description: 'send a form to 127.0.0.1', canAlwaysAllow: false }} />)
		expect(html).toContain('Allow')
		expect(html).toContain('Deny')
		expect(html).not.toContain('Always allow on this site')
	})
})

describe('the tab marks', () => {
	it('outlines a shared tab and badges one that waits', () => {
		const state = { shared: ['a', 'b'], waiting: ['b'], activeShared: 'a' }
		expect(aiTabClasses(state, 'a')).toEqual(['ai-shared'])
		expect(aiTabClasses(state, 'b')).toEqual(['ai-shared', 'ai-waiting'])
		expect(aiTabClasses(state, 'c')).toEqual([])
	})
})

describe('the Settings section', () => {
	it('shows the placeholder in a snippet until Show fetched the token', () => {
		const text = `claude mcp add --header "Authorization: Bearer ${TOKEN_PLACEHOLDER}"`
		expect(withToken(text, null)).toBe(text)
		expect(withToken(text, 'abc123')).toBe('claude mcp add --header "Authorization: Bearer abc123"')
	})

	it('takes only a whole port Zing may listen on', () => {
		expect(parsePort('47821')).toBe(47821)
		expect(parsePort('80')).toBeNull()
		expect(parsePort('70000')).toBeNull()
		expect(parsePort('12a')).toBeNull()
	})
})

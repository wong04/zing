import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { CreditsRowView } from './CreditsRow'

vi.mock('../api', () => ({ zing: { getCredits: vi.fn() } }))

describe('the Credits row', () => {
	it('offers Show while closed and renders no text', () => {
		const html = renderToStaticMarkup(<CreditsRowView state={{ kind: 'closed' }} onToggle={() => {}} />)
		expect(html).toContain('Show')
		expect(html).not.toContain('credits-text')
	})

	it('shows the notices in a scrollable region, with Hide', () => {
		const html = renderToStaticMarkup(<CreditsRowView state={{ kind: 'shown', text: '@mozilla/readability 0.6.0' }} onToggle={() => {}} />)
		expect(html).toContain('Hide')
		expect(html).toContain('aria-label="Credits"')
		expect(html).toContain('@mozilla/readability 0.6.0')
	})

	it('says why the credits could not load', () => {
		const html = renderToStaticMarkup(<CreditsRowView state={{ kind: 'failed', message: 'no such file' }} onToggle={() => {}} />)
		expect(html).toContain('no such file')
	})
})

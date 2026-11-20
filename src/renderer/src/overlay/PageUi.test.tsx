import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { ScriptDialogPrompt } from '@shared/browser/pageUi'
import { ScriptDialogCard } from './PageUi'

vi.mock('../api', () => ({ zing: {} }))
vi.mock('../settings/useAccentTheme', () => ({ useAccentTheme: () => undefined }))

const card = (prompt: Omit<ScriptDialogPrompt, 'id'>): string => renderToStaticMarkup(<ScriptDialogCard prompt={{ id: 1, ...prompt }} />)
const buttons = (html: string): string[] => [...html.matchAll(/<button[^>]*>([^<]*)<\/button>/g)].map((m) => m[1]!)

describe('a page dialog in the page UI', () => {
	it('asks Leave site? with Leave and Stay as a tab or window closes', () => {
		const html = card({ kind: 'beforeunload', origin: 'https://draft.test', message: '' })
		expect(html).toContain('aria-label="Leave site?"')
		expect(html).toContain('<strong>https://draft.test</strong>')
		expect(buttons(html)).toEqual(['Leave', 'Stay'])
	})

	it('shows an alert with OK only, naming who says it', () => {
		const html = card({ kind: 'alert', origin: 'https://a.test', message: 'Saved' })
		expect(html).toContain('https://a.test says')
		expect(html).toContain('Saved')
		expect(buttons(html)).toEqual(['OK'])
	})

	it('offers a prompt’s starting text, and says which hidden tab asks', () => {
		const html = card({ kind: 'prompt', origin: 'null', message: 'Name?', defaultText: 'Ada', tab: 'Inbox' })
		expect(html).toContain('This page says')
		expect(html).toContain('value="Ada"')
		expect(html).toContain('From the tab “Inbox”')
		expect(buttons(html)).toEqual(['OK', 'Cancel'])
	})
})

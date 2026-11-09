import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { PeekState } from '@shared/browser/peek'
import { PeekButtons } from './PeekControls'

const { runCommand } = vi.hoisted(() => ({ runCommand: vi.fn() }))
vi.mock('../api', () => ({ zing: { runCommand } }))

const preview: PeekState = { host: 'docs.google.com', canExpand: true, highContrast: false }

type ButtonProps = { 'aria-label'?: string; onClick?: () => void; children?: ReactNode }

/** The rendered tree's buttons, by accessible name: the view has no DOM in vitest, so clicks are its handlers called directly. */
function buttons(node: ReactNode): Map<string, ButtonProps> {
	const found = new Map<string, ButtonProps>()
	const walk = (n: ReactNode): void => {
		if (Array.isArray(n)) return n.forEach(walk)
		if (!isValidElement(n)) return
		const element = n as ReactElement<ButtonProps>
		if (element.type === 'button' && element.props['aria-label']) found.set(element.props['aria-label'], element.props)
		walk(element.props.children)
	}
	walk(node)
	return found
}

describe('preview buttons', () => {
	beforeEach(() => runCommand.mockClear())

	it('names both buttons and gives each a tooltip with its key', () => {
		const html = renderToStaticMarkup(<PeekButtons peek={preview} />)
		expect(html).toContain('aria-label="Close preview"')
		expect(html).toContain('title="Close preview (Esc)"')
		expect(html).toContain('aria-label="Open as tab"')
		expect(html).toContain('title="Open as tab (Ctrl+O)"')
	})

	it('disables open as tab while it cannot expand', () => {
		const html = renderToStaticMarkup(<PeekButtons peek={{ ...preview, canExpand: false }} />)
		expect(html).toMatch(/<button[^>]*aria-label="Open as tab"[^>]*disabled=""/)
		expect(html).not.toMatch(/<button[^>]*aria-label="Close preview"[^>]*disabled=""/)
		expect(renderToStaticMarkup(<PeekButtons peek={preview} />)).not.toContain('disabled')
	})

	it('announces the preview and how to close it', () => {
		const html = renderToStaticMarkup(<PeekButtons peek={preview} />)
		expect(html).toMatch(/aria-live="polite"[^>]*>Preview of docs.google.com. Press Escape to close.</)
	})

	it('keeps an empty live region and no buttons while no preview is open', () => {
		const html = renderToStaticMarkup(<PeekButtons peek={null} />)
		expect(html).not.toContain('<button')
		expect(html).toContain('<span class="peek-live" aria-live="polite"></span>')
	})

	it('runs preview.close and preview.expand on click', () => {
		const found = buttons(PeekButtons({ peek: preview }))
		found.get('Close preview')!.onClick!()
		found.get('Open as tab')!.onClick!()
		expect(runCommand.mock.calls).toEqual([['preview.close'], ['preview.expand']])
	})

	it('marks high contrast for its styles', () => {
		expect(renderToStaticMarkup(<PeekButtons peek={{ ...preview, highContrast: true }} />)).toContain('class="peek high-contrast"')
		expect(renderToStaticMarkup(<PeekButtons peek={preview} />)).toContain('class="peek"')
	})
})

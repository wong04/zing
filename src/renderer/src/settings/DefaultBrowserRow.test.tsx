import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { DefaultBrowserRowView } from './DefaultBrowserRow'

const { openDefaultAppsSettings } = vi.hoisted(() => ({ openDefaultAppsSettings: vi.fn() }))
vi.mock('../api', () => ({ zing: { openDefaultAppsSettings } }))

type ButtonProps = { onClick?: () => void; children?: ReactNode }

/** The rendered tree's buttons: the view has no DOM in vitest, so a click is the handler called directly. */
function buttons(node: ReactNode): ButtonProps[] {
	const found: ButtonProps[] = []
	const walk = (n: ReactNode): void => {
		if (Array.isArray(n)) return n.forEach(walk)
		if (!isValidElement(n)) return
		const element = n as ReactElement<ButtonProps>
		if (typeof element.type === 'function') return walk((element.type as (p: unknown) => ReactNode)(element.props))
		if (element.type === 'button') found.push(element.props)
		walk(element.props.children)
	}
	walk(node)
	return found
}

describe('the Default browser row', () => {
	beforeEach(() => openDefaultAppsSettings.mockClear())

	it('says so when Zing is the default, with nothing to press', () => {
		const row = <DefaultBrowserRowView state={{ registered: true, isDefault: true }} />
		expect(renderToStaticMarkup(row)).toContain('Zing is your default browser')
		expect(buttons(row)).toEqual([])
	})

	it('offers Make default when installed but not the default, which opens Windows Settings', () => {
		const row = <DefaultBrowserRowView state={{ registered: true, isDefault: false }} />
		const html = renderToStaticMarkup(row)
		expect(html).toContain('Make default')
		expect(html).not.toContain('Zing is your default browser')
		const [button, ...rest] = buttons(row)
		expect(rest).toEqual([])
		button!.onClick!()
		expect(openDefaultAppsSettings).toHaveBeenCalledTimes(1)
	})

	it('asks for an install when Zing is not registered', () => {
		const row = <DefaultBrowserRowView state={{ registered: false, isDefault: false }} />
		expect(renderToStaticMarkup(row)).toContain('Install Zing to set it as default')
		expect(buttons(row)).toEqual([])
	})

	it('shows only the title until the host answers', () => {
		const html = renderToStaticMarkup(<DefaultBrowserRowView state={null} />)
		expect(html).toContain('Default browser')
		expect(html).not.toContain('Make default')
		expect(html).not.toContain('Install Zing')
	})
})

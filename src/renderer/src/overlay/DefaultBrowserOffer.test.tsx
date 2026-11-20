import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { DefaultBrowserOffer } from './DefaultBrowserOffer'

const api = vi.hoisted(() => ({
	openDefaultAppsSettings: vi.fn(() => Promise.resolve()),
	dismissOffer: vi.fn(() => Promise.resolve()),
	dismissDefaultBrowserOffer: vi.fn(() => Promise.resolve()),
}))
vi.mock('../api', () => ({ zing: api }))

type ButtonProps = { onClick?: () => void; children?: ReactNode }

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

const label = (button: ButtonProps): string => renderToStaticMarkup(<>{button.children}</>)

describe('the default-browser offer', () => {
	beforeEach(() => Object.values(api).forEach((fn) => fn.mockClear()))

	it('asks the question with Make default and Not now', () => {
		const offer = <DefaultBrowserOffer />
		expect(renderToStaticMarkup(offer)).toContain('Make Zing your default browser?')
		expect(buttons(offer).map(label)).toEqual(['Make default', 'Not now'])
	})

	it('Make default opens Windows Settings and takes the offer down', async () => {
		const [make] = buttons(<DefaultBrowserOffer />)
		make!.onClick!()
		await vi.waitFor(() => expect(api.dismissOffer).toHaveBeenCalledTimes(1))
		expect(api.openDefaultAppsSettings).toHaveBeenCalledTimes(1)
		expect(api.dismissDefaultBrowserOffer).not.toHaveBeenCalled()
	})

	it('Not now takes the offer down', () => {
		const [, never] = buttons(<DefaultBrowserOffer />)
		never!.onClick!()
		expect(api.dismissDefaultBrowserOffer).toHaveBeenCalledTimes(1)
		expect(api.openDefaultAppsSettings).not.toHaveBeenCalled()
	})
})

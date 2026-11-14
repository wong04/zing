import { isValidElement, type ReactElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { FullScreenExitButton } from './FullScreenExit'

const { exitFullScreen } = vi.hoisted(() => ({ exitFullScreen: vi.fn(() => Promise.resolve()) }))
vi.mock('../api', () => ({ zing: { exitFullScreen } }))

describe('the Exit full screen button', () => {
	it('is named for a screen reader and leaves fullscreen on click', () => {
		const button = FullScreenExitButton()
		expect(renderToStaticMarkup(button)).toContain('aria-label="Exit full screen"')
		expect(isValidElement(button)).toBe(true)
		;(button as ReactElement<{ onClick: () => void }>).props.onClick()
		expect(exitFullScreen).toHaveBeenCalledTimes(1)
	})
})

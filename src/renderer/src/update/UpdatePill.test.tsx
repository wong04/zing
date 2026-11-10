import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { UpdateState, UpdateStatus } from '@shared/ipc'
import { UpdatePillButton } from './UpdatePill'
import { checkStatusText } from './updateText'

const { relaunchToUpdate } = vi.hoisted(() => ({ relaunchToUpdate: vi.fn() }))
vi.mock('../api', () => ({ zing: { relaunchToUpdate } }))

const state = (status: UpdateStatus): UpdateState => ({ status, currentVersion: '0.9.0', ...(status === 'ready' ? { readyVersion: '0.9.1' } : {}) })

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

describe('UpdatePill', () => {
	beforeEach(() => relaunchToUpdate.mockClear())

	it('renders only when ready and invokes update:relaunch once on click', () => {
		for (const status of ['idle', 'checking', 'downloading', 'upToDate', 'failed', 'unavailable'] as const) {
			expect(renderToStaticMarkup(<UpdatePillButton state={state(status)} />), status).toBe('')
		}
		expect(renderToStaticMarkup(<UpdatePillButton state={null} />)).toBe('')

		const ready = <UpdatePillButton state={state('ready')} />
		expect(renderToStaticMarkup(ready)).toContain('Relaunch to update')
		const [button, ...rest] = buttons(ready)
		expect(rest).toEqual([])
		button!.onClick!()
		expect(relaunchToUpdate).toHaveBeenCalledTimes(1)
	})
})

describe('the About check status', () => {
	it('says what the last check found', () => {
		expect(checkStatusText(state('idle'))).toBeUndefined()
		expect(checkStatusText(state('checking'))).toBe('Checking…')
		expect(checkStatusText(state('upToDate'))).toBe("You're up to date")
		expect(checkStatusText(state('downloading'))).toBe('Downloading…')
		expect(checkStatusText(state('ready'))).toBe('Ready: relaunch to update')
		expect(checkStatusText(state('failed'))).toBe("Couldn't check")
		expect(checkStatusText(state('unavailable'))).toBe('Updates work in installed builds')
	})
})

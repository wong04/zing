import type { WebContents } from 'electron'
import { describe, expect, it, vi } from 'vitest'
import { EMPTY_PAGE_UI, type PageUiState } from '@shared/browser/pageUi'
import { PermissionBroker } from './permissions'
import type { PageUiHost } from './pageui-layer'

function setup() {
	let state: PageUiState = EMPTY_PAGE_UI
	const host: PageUiHost = {
		state: () => state,
		set: (patch) => {
			state = { ...state, ...patch }
		},
		focus: () => undefined,
	}
	const contents = { getURL: () => 'https://a.test/' } as unknown as WebContents
	return { broker: new PermissionBroker(host), contents, state: () => state }
}

describe('PermissionBroker', () => {
	it('refuses what it does not prompt for', () => {
		const { broker, contents } = setup()
		const callback = vi.fn()
		broker.request(contents, 'midi', {}, callback)
		expect(callback).toHaveBeenCalledWith(false)
	})

	it('allows fullscreen without asking', () => {
		const { broker, contents } = setup()
		const callback = vi.fn()
		broker.request(contents, 'fullscreen', {}, callback)
		expect(callback).toHaveBeenCalledWith(true)
	})

	it('holds a prompt until answered, then remembers the grant for that tab', () => {
		const { broker, contents, state } = setup()
		const first = vi.fn()
		broker.request(contents, 'geolocation', { requestingUrl: 'https://a.test/x' }, first)
		expect(first).not.toHaveBeenCalled()
		expect(state().permission?.kind).toBe('location')
		broker.answer(state().permission!.id, true)
		expect(first).toHaveBeenCalledWith(true)
		expect(state().permission).toBeUndefined()

		const second = vi.fn()
		broker.request(contents, 'geolocation', { requestingUrl: 'https://a.test/y' }, second)
		expect(second).toHaveBeenCalledWith(true)
		expect(broker.check(contents, 'geolocation', 'https://a.test', undefined)).toBe(true)
	})

	it('denies and does not remember on Deny', () => {
		const { broker, contents, state } = setup()
		const callback = vi.fn()
		broker.request(contents, 'notifications', {}, callback)
		broker.answer(state().permission!.id, false)
		expect(callback).toHaveBeenCalledWith(false)
		expect(broker.check(contents, 'notifications', 'https://a.test', undefined)).toBe(false)
	})

	it('labels camera and microphone together', () => {
		const { broker, contents, state } = setup()
		broker.request(contents, 'media', { mediaTypes: ['video', 'audio'] }, vi.fn())
		expect(state().permission?.kind).toBe('camera and microphone')
	})
})

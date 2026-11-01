import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { RendererErrorReport } from '@shared/ipc'
import { reportingInvoke } from '@shared/bridge/reportingInvoke'
import { HostError } from '@shared/bridge/webviewBridge'
import { createZingApi, type Invoke, type Listen } from '@shared/zingApi'
import { createErrorReporter, describe as describeError, installErrorReporting, viewName, type ErrorEventTarget, type ErrorReporterOptions } from './errors'

type ErrorListener = (event: { readonly error?: unknown; readonly message?: string }) => void
type RejectionListener = (event: { readonly reason: unknown; preventDefault(): void }) => void

function fakeWindow(): ErrorEventTarget & { fireError: ErrorListener; fireRejection: (reason: unknown) => { prevented: boolean } } {
	let onError: ErrorListener = () => undefined
	let onRejection: RejectionListener = () => undefined
	return {
		addEventListener: ((type: string, listener: ErrorListener & RejectionListener) => {
			if (type === 'error') onError = listener
			else onRejection = listener
		}) as ErrorEventTarget['addEventListener'],
		fireError: (event) => onError(event),
		fireRejection: (reason) => {
			const result = { prevented: false }
			onRejection({ reason, preventDefault: () => (result.prevented = true) })
			return result
		},
	}
}

function setup(overrides: Partial<ErrorReporterOptions> = {}) {
	const sent: RendererErrorReport[] = []
	const reporter = createErrorReporter({
		view: 'chrome',
		send: (report) => {
			sent.push(report)
			return Promise.resolve()
		},
		now: () => Date.now(),
		...overrides,
	})
	return { sent, reporter }
}

beforeEach(() => {
	vi.useFakeTimers()
	vi.setSystemTime(0)
})

afterEach(() => {
	vi.useRealTimers()
	vi.restoreAllMocks()
})

describe('error reporter', () => {
	it('reports an uncaught error with its view and stack', () => {
		const { sent, reporter } = setup()
		const error = new TypeError('x is undefined')

		reporter.error(error)

		expect(sent).toEqual([{ kind: 'error', message: 'TypeError: x is undefined', stack: error.stack, view: 'chrome' }])
	})

	it('reports an error event that has only a message', () => {
		const { sent, reporter } = setup()

		reporter.error(null, 'Script error.')

		expect(sent).toEqual([{ kind: 'error', message: 'Script error.', view: 'chrome' }])
	})

	it('reports a failed host call only once it goes unhandled, with its channel once in the message', () => {
		const { sent, reporter } = setup()
		const named = new HostError('KeyNotFoundException', 'no tab', 'tabs:close')
		const bare = new Error('broken')

		reporter.bridge('tabs:close', named)
		reporter.bridge('tabs:get', bare)
		expect(sent).toEqual([])

		reporter.rejection(named)
		reporter.rejection(bare)
		expect(sent.map((r) => [r.kind, r.message])).toEqual([
			['bridge', 'KeyNotFoundException: tabs:close: no tab'],
			['bridge', 'tabs:get: Error: broken'],
		])
	})

	it('cuts a flood of the same message to the limit per window, then says how many it left out', () => {
		const { sent, reporter } = setup({ limit: 2, windowMs: 1000 })

		for (let i = 0; i < 50; i++) reporter.error(new Error('loop'))
		reporter.error(new Error('other'))
		expect(sent.map((r) => r.message)).toEqual(['Error: loop', 'Error: loop', 'Error: other'])

		vi.advanceTimersByTime(999)
		reporter.error(new Error('loop'))
		expect(sent).toHaveLength(3)

		vi.advanceTimersByTime(1)
		reporter.error(new Error('loop'))
		expect(sent.map((r) => r.message)).toEqual(['Error: loop', 'Error: loop', 'Error: other', 'Error: loop (and 49 more like it)'])
	})

	it('only logs a report it could not send, and never reports that failure', async () => {
		const log = vi.fn()
		const send = vi.fn(() => Promise.reject(new Error('bridge gone')))
		const { reporter } = setup({ send, log })

		reporter.error(new Error('first'))
		await vi.runAllTimersAsync()

		expect(send).toHaveBeenCalledTimes(1)
		expect(log).toHaveBeenCalledTimes(1)
	})

	it('survives a send that throws instead of rejecting', () => {
		const log = vi.fn()
		const { reporter } = setup({
			send: () => {
				throw new Error('no bridge')
			},
			log,
		})

		expect(() => reporter.error(new Error('first'))).not.toThrow()
		expect(log).toHaveBeenCalledTimes(1)
	})
})

describe('installErrorReporting', () => {
	it('reports error events and unhandled rejections', () => {
		const { sent, reporter } = setup()
		const target = fakeWindow()
		installErrorReporting(target, reporter)

		target.fireError({ error: new Error('boom'), message: 'Uncaught Error: boom' })
		const outcome = target.fireRejection(new Error('nobody caught me'))

		expect(sent.map((r) => [r.kind, r.message])).toEqual([
			['error', 'Error: boom'],
			['unhandledrejection', 'Error: nobody caught me'],
		])
		expect(outcome.prevented).toBe(false)
	})

	it('a voided failed host call is reported exactly once, as a bridge failure', async () => {
		const { sent, reporter } = setup()
		const target = fakeWindow()
		installErrorReporting(target, reporter)
		const error = new HostError('InvalidOperationException', 'window closed', 'tabs:close')
		const listen: Listen = () => () => undefined
		const zing = createZingApi(
			reportingInvoke((() => Promise.reject(error)) as Invoke, reporter.bridge),
			listen,
		)

		// What `void zing.closeTab(id)` leaves behind: a rejection the browser then raises as unhandled.
		const reason = await zing.closeTab('t1').catch((e: unknown) => e)
		const outcome = target.fireRejection(reason)

		expect(reason).toBe(error)
		expect(sent).toEqual([{ kind: 'bridge', message: 'InvalidOperationException: tabs:close: window closed', stack: error.stack, view: 'chrome' }])
		expect(outcome.prevented).toBe(true)
	})

	it('a failed host call its caller awaits and catches is never reported', async () => {
		const { sent, reporter } = setup()
		installErrorReporting(fakeWindow(), reporter)
		const refusal = new HostError('InvalidSettingException', 'Invalid value for setting "searchEngine"', 'settings:set')
		const zing = createZingApi(
			reportingInvoke((() => Promise.reject(refusal)) as Invoke, reporter.bridge),
			() => () => undefined,
		)

		await expect(zing.setSetting('searchEngine', 'altavista')).rejects.toBe(refusal)
		await vi.runAllTimersAsync()

		expect(sent).toEqual([])
	})

	it('does not recurse when reporting itself fails', async () => {
		const log = vi.fn()
		const target = fakeWindow()
		const failure = new Error('host unreachable')
		const invoke = vi.fn(() => Promise.reject(failure)) as unknown as Invoke
		// The real wiring: the reporter sends through the same wrapped api it reports for.
		const reporter = createErrorReporter({ view: 'overlay', send: (report) => zing.reportError(report), now: () => Date.now(), log })
		const zing = createZingApi(
			reportingInvoke(invoke, reporter.bridge),
			() => () => undefined,
		)
		installErrorReporting(target, reporter)

		const reason = await zing.closeTab('t1').catch((e: unknown) => e)
		target.fireRejection(reason)
		await vi.runAllTimersAsync()

		// One failed call, one attempt to report it, which only reaches the console.
		expect(invoke).toHaveBeenCalledTimes(2)
		expect(invoke).toHaveBeenLastCalledWith('app:report-error', expect.objectContaining({ kind: 'bridge', view: 'overlay' }))
		expect(log).toHaveBeenCalledTimes(1)
	})
})

describe('helpers', () => {
	it('names the view from its hash', () => {
		expect(viewName('')).toBe('chrome')
		expect(viewName('#chrome')).toBe('chrome')
		expect(viewName('#overlay-stack')).toBe('overlay-stack')
		expect(viewName('#pageui')).toBe('pageui')
	})

	it('describes anything thrown', () => {
		expect(describeError(new RangeError('bad'))).toBe('RangeError: bad')
		expect(describeError('plain')).toBe('plain')
		expect(describeError({ code: 3 })).toBe('{"code":3}')
		expect(describeError(undefined)).toBe('undefined')
	})
})

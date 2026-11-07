import { describe, expect, it, vi } from 'vitest'
import type { InvokeChannel } from '../ipc'
import { createZingApi, type Invoke, type Listen } from '../zingApi'
import { reportingInvoke } from './reportingInvoke'
import { HostError, webViewZingApi, type InvokeMessage, type WebViewChannel } from './webviewBridge'

const listen: Listen = () => () => undefined

function failing(error: unknown): Invoke {
	return (() => Promise.reject(error)) as Invoke
}

describe('reportingInvoke', () => {
	it('reports a failed call once with its channel, and still rejects with the same error', async () => {
		const error = new HostError('InvalidOperationException', 'no such tab', 'tabs:close')
		const onFailure = vi.fn()
		const zing = createZingApi(reportingInvoke(failing(error), onFailure), listen)

		await expect(zing.closeTab('t1')).rejects.toBe(error)
		expect(onFailure).toHaveBeenCalledTimes(1)
		expect(onFailure).toHaveBeenCalledWith('tabs:close', error)
	})

	it('passes results through untouched', async () => {
		const onFailure = vi.fn()
		const invoke = vi.fn(() => Promise.resolve({ tabs: [] })) as unknown as Invoke
		const zing = createZingApi(reportingInvoke(invoke, onFailure), listen)

		await expect(zing.getTabs()).resolves.toEqual({ tabs: [] })
		expect(invoke).toHaveBeenCalledWith('tabs:get')
		expect(onFailure).not.toHaveBeenCalled()
	})

	it('turns a call that throws before it has a promise into a reported rejection', async () => {
		const error = new Error('postMessage failed')
		const onFailure = vi.fn()
		const invoke = (() => {
			throw error
		}) as Invoke
		const zing = createZingApi(reportingInvoke(invoke, onFailure), listen)

		await expect(zing.closeTab('t1')).rejects.toBe(error)
		expect(onFailure).toHaveBeenCalledWith('tabs:close', error)
	})

	it('never reports a failed report through itself', async () => {
		const error = new Error('bridge gone')
		const onFailure = vi.fn()
		const zing = createZingApi(reportingInvoke(failing(error), onFailure), listen)

		await expect(zing.reportError({ kind: 'error', message: 'x' })).rejects.toBe(error)
		expect(onFailure).not.toHaveBeenCalled()
	})

	it('still rejects with the original error when the reporter itself throws', async () => {
		const error = new Error('host failed')
		const log = vi.spyOn(console, 'error').mockImplementation(() => undefined)
		const zing = createZingApi(
			reportingInvoke(failing(error), () => {
				throw new Error('reporter broke')
			}),
			listen,
		)

		await expect(zing.closeTab('t1')).rejects.toBe(error)
		expect(log).toHaveBeenCalledTimes(1)
		log.mockRestore()
	})

	it('wraps the WebView2 bridge’s calls, reporting a host error reply', async () => {
		const sent: InvokeMessage[] = []
		let deliver: (data: unknown) => void = () => undefined
		const webview: WebViewChannel = {
			postMessage: (message) => sent.push(message as InvokeMessage),
			postMessageWithAdditionalObjects: () => undefined,
			addEventListener: (_type, listener) => {
				deliver = (data) => listener({ data })
			},
		}
		const failures: [InvokeChannel, unknown][] = []
		const zing = webViewZingApi(webview, (invoke) => reportingInvoke(invoke, (channel, error) => failures.push([channel, error])))

		const closing = zing.closeTab('t1')
		deliver({ kind: 'result', id: sent[0]!.id, ok: false, error: { name: 'KeyNotFoundException', message: 'no tab t1' } })

		await expect(closing).rejects.toThrow('tabs:close: no tab t1')
		expect(failures).toHaveLength(1)
		expect(failures[0]![0]).toBe('tabs:close')
	})
})

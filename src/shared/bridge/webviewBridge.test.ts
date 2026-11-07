import { describe, expect, it } from 'vitest'
import { BridgeProtocolError, HostError, webViewZingApi, type InvokeMessage, type WebViewChannel } from './webviewBridge'

function fakeWebView(): WebViewChannel & { sent: InvokeMessage[]; objects: unknown[][]; deliver: (data: unknown) => void } {
	const sent: InvokeMessage[] = []
	const objects: unknown[][] = []
	let listener: ((event: { data: unknown }) => void) | undefined
	return {
		sent,
		objects,
		postMessage: (message) => sent.push(message as InvokeMessage),
		postMessageWithAdditionalObjects: (message, files) => {
			sent.push(message as InvokeMessage)
			objects.push([...files])
		},
		addEventListener: (_type, l) => {
			listener = l
		},
		deliver: (data) => listener!({ data }),
	}
}

describe('WebView2 bridge', () => {
	it('sends an invoke and resolves with the host’s value', async () => {
		const webview = fakeWebView()
		const zing = webViewZingApi(webview)
		const tabs = zing.getTabs()
		expect(webview.sent).toEqual([{ kind: 'invoke', id: 1, channel: 'tabs:get', args: [] }])
		webview.deliver({ kind: 'result', id: 1, ok: true, value: { tabs: [], activeId: undefined } })
		await expect(tabs).resolves.toEqual({ tabs: [], activeId: undefined })
	})

	it('sends dropped files as additional objects so the host reads their paths, and none of them in the JSON', async () => {
		const webview = fakeWebView()
		const zing = webViewZingApi(webview)
		const files = [{ name: 'a.mov' }, { name: 'b.png' }] as unknown as File[]
		const dropped = zing.openDropped(files)
		expect(webview.sent).toEqual([{ kind: 'invoke', id: 1, channel: 'files:open-dropped', args: [] }])
		expect(webview.objects).toEqual([files])
		webview.deliver({ kind: 'result', id: 1, ok: true, value: [{ token: 't1', name: 'a.mov' }] })
		await expect(dropped).resolves.toEqual([{ token: 't1', name: 'a.mov' }])
	})

	it('passes arguments in order on the same channels as Electron', async () => {
		const webview = fakeWebView()
		const zing = webViewZingApi(webview)
		void zing.navigateTab('tab-1', 'example.com')
		void zing.moveTab('tab-1', 3)
		expect(webview.sent.map((m) => [m.channel, m.args])).toEqual([
			['tabs:navigate', ['tab-1', 'example.com']],
			['tabs:move', ['tab-1', 3]],
		])
	})

	it('rejects with the host’s error name and the channel', async () => {
		const webview = fakeWebView()
		const zing = webViewZingApi(webview)
		const result = zing.getSettings()
		webview.deliver({ kind: 'result', id: 1, ok: false, error: { name: 'NotImplementedError', message: 'not on WebView2 yet' } })
		await expect(result).rejects.toBeInstanceOf(HostError)
		await expect(result).rejects.toMatchObject({ name: 'NotImplementedError', message: 'settings:get: not on WebView2 yet' })
	})

	it('answers interleaved requests by id', async () => {
		const webview = fakeWebView()
		const zing = webViewZingApi(webview)
		const first = zing.getTabs()
		const second = zing.getSettings()
		webview.deliver({ kind: 'result', id: 2, ok: true, value: 'settings' })
		webview.deliver({ kind: 'result', id: 1, ok: true, value: 'tabs' })
		await expect(Promise.all([first, second])).resolves.toEqual(['tabs', 'settings'])
	})

	it('delivers events to listeners until they unsubscribe', () => {
		const webview = fakeWebView()
		const zing = webViewZingApi(webview)
		const seen: unknown[] = []
		const unsubscribe = zing.onTabsState((state) => seen.push(state))
		webview.deliver({ kind: 'event', channel: 'tabs:state', payload: 1 })
		unsubscribe()
		webview.deliver({ kind: 'event', channel: 'tabs:state', payload: 2 })
		expect(seen).toEqual([1])
	})

	it('an event pushed before anything listens reaches the first listener, latest only, once', () => {
		const webview = fakeWebView()
		const zing = webViewZingApi(webview)
		webview.deliver({ kind: 'event', channel: 'overlay:notice', payload: { id: 1, text: 'old' } })
		webview.deliver({ kind: 'event', channel: 'overlay:notice', payload: { id: 2, text: 'new' } })
		const first: unknown[] = []
		const second: unknown[] = []
		zing.onNotice((n) => first.push(n))
		zing.onNotice((n) => second.push(n))
		expect(first).toEqual([{ id: 2, text: 'new' }])
		expect(second).toEqual([])
	})

	it('a result nobody asked for is a protocol error, not silently dropped', () => {
		const webview = fakeWebView()
		webViewZingApi(webview)
		expect(() => webview.deliver({ kind: 'result', id: 99, ok: true, value: null })).toThrow(BridgeProtocolError)
	})
})

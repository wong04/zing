// The renderer side of the WebView2 host bridge. Same ZingApi as Electron,
// carried over `window.chrome.webview` messages instead of ipcRenderer.
//
// Protocol (JSON objects both ways; the host's side is host/Zing.Core/Bridge):
//   renderer -> host  { kind: 'invoke', id, channel, args }
//   host -> renderer  { kind: 'result', id, ok: true, value }
//                     { kind: 'result', id, ok: false, error: { name, message } }
//                     { kind: 'event', channel, payload }
import type { EventChannel, EventContract, InvokeChannel, InvokeContract, Unsubscribe, ZingApi } from '../ipc'
import { createZingApi } from '../zingApi'

/** The slice of `window.chrome.webview` the bridge uses. */
export interface WebViewChannel {
	postMessage(message: unknown): void
	addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void
}

export interface InvokeMessage {
	readonly kind: 'invoke'
	readonly id: number
	readonly channel: InvokeChannel
	readonly args: readonly unknown[]
}

type HostMessage =
	| { readonly kind: 'result'; readonly id: number; readonly ok: true; readonly value: unknown }
	| { readonly kind: 'result'; readonly id: number; readonly ok: false; readonly error: { readonly name: string; readonly message: string } }
	| { readonly kind: 'event'; readonly channel: EventChannel; readonly payload: unknown }

/** An error the host raised while answering, with the host's own error name. */
export class HostError extends Error {
	constructor(
		override readonly name: string,
		message: string,
		readonly channel: InvokeChannel,
	) {
		super(`${channel}: ${message}`)
	}
}

export class BridgeProtocolError extends Error {
	override name = 'BridgeProtocolError'
}

interface Pending {
	readonly channel: InvokeChannel
	readonly resolve: (value: unknown) => void
	readonly reject: (error: Error) => void
}

export function webViewZingApi(webview: WebViewChannel): ZingApi {
	let nextId = 1
	const pending = new Map<number, Pending>()
	const listeners = new Map<EventChannel, Set<(payload: unknown) => void>>()

	webview.addEventListener('message', ({ data }) => receive(data as HostMessage))

	function receive(message: HostMessage): void {
		if (message.kind === 'event') {
			for (const listener of listeners.get(message.channel) ?? []) listener(message.payload)
			return
		}
		if (message.kind !== 'result') throw new BridgeProtocolError(`Unknown message from the host: ${JSON.stringify(message)}`)
		const waiting = pending.get(message.id)
		if (!waiting) throw new BridgeProtocolError(`The host answered request ${message.id}, which nothing is waiting for`)
		pending.delete(message.id)
		if (message.ok) waiting.resolve(message.value)
		else waiting.reject(new HostError(message.error.name, message.error.message, waiting.channel))
	}

	const invoke = <C extends InvokeChannel>(channel: C, ...args: InvokeContract[C]['args']): Promise<InvokeContract[C]['result']> =>
		new Promise((resolve, reject) => {
			const id = nextId++
			pending.set(id, { channel, resolve: resolve as (value: unknown) => void, reject })
			const message: InvokeMessage = { kind: 'invoke', id, channel, args }
			webview.postMessage(message)
		})

	const listen = <C extends EventChannel>(channel: C, listener: (payload: EventContract[C]) => void): Unsubscribe => {
		const set = listeners.get(channel) ?? new Set()
		listeners.set(channel, set)
		const wrapped = listener as (payload: unknown) => void
		set.add(wrapped)
		return () => set.delete(wrapped)
	}

	return createZingApi(invoke, listen)
}

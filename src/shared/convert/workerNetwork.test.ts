import { describe, expect, it, vi } from 'vitest'
import { isAllowedWorkerUrl, restrictNetwork } from './workerNetwork'

const own = 'https://app.zing'
const host = 'https://view.zing'

describe('isAllowedWorkerUrl', () => {
	it('allows the app bundle and the host file server, and nothing else', () => {
		expect(isAllowedWorkerUrl('https://app.zing/assets/ort-wasm.wasm', own, host)).toBe(true)
		expect(isAllowedWorkerUrl('https://view.zing/model/bgmodel', own, host)).toBe(true)
		expect(isAllowedWorkerUrl('/assets/x.wasm', own, host, `${own}/assets/w.js`)).toBe(true)
		expect(isAllowedWorkerUrl('https://cdn.jsdelivr.net/npm/onnxruntime-web/dist/x.wasm', own, host)).toBe(false)
		expect(isAllowedWorkerUrl('https://app.zing.evil.example/x', own, host)).toBe(false)
		expect(isAllowedWorkerUrl('http://app.zing/x', own, host)).toBe(false)
		expect(isAllowedWorkerUrl('https://view.zing:8443/x', own, host)).toBe(false)
		expect(isAllowedWorkerUrl('data:text/plain,hi', own, host)).toBe(false)
		expect(isAllowedWorkerUrl('blob:https://app.zing/abc', own, host)).toBe(false)
		expect(isAllowedWorkerUrl('https://', own, host)).toBe(false)
	})
})

describe('restrictNetwork', () => {
	it('lets an allowed fetch through and rejects any other before it reaches the network', async () => {
		const real = vi.fn(() => Promise.resolve('ok'))
		const scope: Record<string, unknown> = { fetch: real, XMLHttpRequest: class {}, WebSocket: class {}, EventSource: class {}, location: { href: 'https://app.zing/assets/w.js' } }
		restrictNetwork(scope, own, host)
		await expect((scope.fetch as (u: string) => Promise<string>)('https://view.zing/model/bgmodel')).resolves.toBe('ok')
		await expect((scope.fetch as (u: string) => Promise<string>)('https://cdn.example/x.wasm')).rejects.toThrow(/not allowed/)
		expect(real).toHaveBeenCalledTimes(1)
	})
	it('removes the other ways to reach the network', () => {
		const scope: Record<string, unknown> = { fetch: () => Promise.resolve(), XMLHttpRequest: class {}, WebSocket: class {}, EventSource: class {}, location: { href: 'https://app.zing/a.js' } }
		restrictNetwork(scope, own, host)
		expect(scope.XMLHttpRequest).toBeUndefined()
		expect(scope.WebSocket).toBeUndefined()
		expect(scope.EventSource).toBeUndefined()
	})
	it('checks a Request object by its url', async () => {
		const scope: Record<string, unknown> = { fetch: () => Promise.resolve('ok'), location: { href: 'https://app.zing/a.js' } }
		restrictNetwork(scope, own, host)
		await expect((scope.fetch as (r: unknown) => Promise<string>)({ url: 'https://evil.example/' })).rejects.toThrow(/not allowed/)
	})
})

/**
 * Whether a worker may ask for this address: the app's own bundle or the host's file server, over https, on their default ports.
 * Data and blob addresses are not allowed, so a script cannot fetch what it built.
 */
export function isAllowedWorkerUrl(url: string, ownOrigin: string, hostOrigin: string, base: string = `${ownOrigin}/`): boolean {
	let parsed: URL
	try {
		parsed = new URL(url, base)
	} catch {
		return false
	}
	return parsed.protocol === 'https:' && (parsed.origin === ownOrigin || parsed.origin === hostOrigin)
}

export class NetworkNotAllowedError extends Error {
	override name = 'NetworkNotAllowedError'
	constructor(url: string) {
		super(`The background remover worker is not allowed to reach ${url}`)
	}
}

/**
 * Narrows what a worker can do over the network, in the worker itself: `fetch` refuses any address but the two allowed ones, and the
 * other ways out (XMLHttpRequest, WebSocket, EventSource) are removed. A worker loaded from the app bundle has no Content-Security-Policy
 * (the bundle is served without headers, and a page's meta policy does not reach a worker), so this is what keeps a library that
 * decides to fetch a script or file from elsewhere from doing so. It is a guard against surprises in code Zing ships, not a
 * boundary against code that deliberately saves the original `fetch` first; a dynamic `import()` is not covered either.
 */
export function restrictNetwork(scope: Record<string, unknown>, ownOrigin: string, hostOrigin: string): void {
	const real = scope.fetch as (input: unknown, init?: unknown) => Promise<unknown>
	const base = (scope.location as { href: string }).href
	scope.fetch = (input: unknown, init?: unknown): Promise<unknown> => {
		const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as { url: string }).url
		if (!isAllowedWorkerUrl(url, ownOrigin, hostOrigin, base)) return Promise.reject(new NetworkNotAllowedError(url))
		return real(input, init)
	}
	for (const name of ['XMLHttpRequest', 'WebSocket', 'EventSource', 'importScripts']) scope[name] = undefined
}

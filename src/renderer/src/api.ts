import type { ZingApi } from '@shared/ipc'
import { webViewZingApi, type WebViewChannel } from '@shared/bridge/webviewBridge'

/**
 * The renderer's only way to the host. Under Electron the preload has put
 * `window.zing` in place; under the WebView2 host the same API is built over
 * `window.chrome.webview`. Neither present means the page was opened outside
 * Zing, which is an error rather than something to limp along without.
 */
function hostApi(): ZingApi {
	if ('zing' in window) return window.zing
	const webview = (window as { chrome?: { webview?: WebViewChannel } }).chrome?.webview
	if (!webview) throw new Error('No Zing host: neither the Electron preload nor WebView2 is present')
	// Where the preload puts it, so both hosts look the same to e2e tests and devtools.
	const api = webViewZingApi(webview)
	Object.defineProperty(window, 'zing', { value: api })
	return api
}

export const zing: ZingApi = hostApi()

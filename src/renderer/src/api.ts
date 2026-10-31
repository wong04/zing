import type { ZingApi } from '@shared/ipc'
import { webViewZingApi, type WebViewChannel } from '@shared/bridge/webviewBridge'

/**
 * The renderer's only way to the host: the WebView2 host's bridge over
 * `window.chrome.webview`. Its absence means the page was opened outside
 * Zing, which is an error rather than something to limp along without.
 */
function hostApi(): ZingApi {
	const webview = (window as { chrome?: { webview?: WebViewChannel } }).chrome?.webview
	if (!webview) throw new Error('No Zing host: this page only runs inside Zing')
	const api = webViewZingApi(webview)
	// On window too, for the e2e tests and devtools.
	Object.defineProperty(window, 'zing', { value: api })
	return api
}

export const zing: ZingApi = hostApi()

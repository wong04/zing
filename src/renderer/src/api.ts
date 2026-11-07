import type { ZingApi } from '@shared/ipc'
import { reportingInvoke } from '@shared/bridge/reportingInvoke'
import { webViewZingApi, type WebViewChannel } from '@shared/bridge/webviewBridge'
import { createErrorReporter, installErrorReporting, viewName, type ErrorReporter } from './errors'

/**
 * The renderer's only way to the host: the WebView2 host's bridge over
 * `window.chrome.webview`. Its absence means the page was opened outside
 * Zing, which is an error rather than something to limp along without.
 *
 * This module is the view's bootstrap for errors too: every module that talks to
 * the host imports it, so its reporter is listening before any of them runs.
 * A failed host call is tagged here and reported once if nobody handles it, so call sites can `void` it.
 */
function hostApi(): { api: ZingApi; reporter: ErrorReporter } {
	const webview = (window as { chrome?: { webview?: WebViewChannel } }).chrome?.webview
	if (!webview) throw new Error('No Zing host: this page only runs inside Zing')
	const reporter = createErrorReporter({
		view: viewName(location.hash),
		send: (report) => api.reportError(report),
		now: () => performance.now(),
	})
	const api = webViewZingApi(webview, (invoke) => reportingInvoke(invoke, reporter.bridge))
	installErrorReporting(window, reporter)
	// On window too, for the e2e tests and devtools.
	Object.defineProperty(window, 'zing', { value: api })
	return { api, reporter }
}

const host = hostApi()
export const zing: ZingApi = host.api
/** The view's reporter, for errors that never reach window: those an error boundary catches. */
export const errorReporter: ErrorReporter = host.reporter

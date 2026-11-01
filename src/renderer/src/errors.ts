import type { InvokeChannel, RendererErrorReport } from '@shared/ipc'

/**
 * Forwards the renderer's uncaught errors and failed host calls to the host
 * (`app:report-error`), which records them: e2e runs fail on them and real
 * installs log them. One reporter per view, made in `api.ts`.
 *
 * - A failed host call is reported once, as `bridge`, where it fails
 *   (`reportingInvoke`). Its error is marked, so the unhandled rejection a
 *   `void zing.x()` caller then leaves is not reported a second time.
 * - A flood of the same message is cut to `limit` reports per `windowMs`; the
 *   next report after the flood says how many were left out.
 * - A report that fails to send only goes to the console, never back to the
 *   host, so a broken bridge can't loop.
 */
export interface ErrorReporterOptions {
	/** The view's root, e.g. `chrome` or `overlay-stack`. */
	readonly view: string
	readonly send: (report: RendererErrorReport) => Promise<void>
	/** Milliseconds, monotonic. */
	readonly now: () => number
	readonly limit?: number
	readonly windowMs?: number
	readonly log?: (...data: unknown[]) => void
}

export interface ErrorReporter {
	/** A host call on `channel` failed. */
	bridge(channel: InvokeChannel, error: unknown): void
	/** An uncaught error (`ErrorEvent`): its `error`, or just its message when the browser gives no object. */
	error(error: unknown, message?: string): void
	/** An unhandled rejection; returns false (sending nothing) when its reason was already reported. */
	rejection(reason: unknown): boolean
	isReported(reason: unknown): boolean
}

interface Flood {
	start: number
	count: number
	suppressed: number
}

const MAX_FLOODS = 100

export function createErrorReporter(options: ErrorReporterOptions): ErrorReporter {
	const { view, send, now, limit = 3, windowMs = 1000, log = console.error } = options
	const reported = new WeakSet<object>()
	const floods = new Map<string, Flood>()

	/** Undefined when this report is part of a flood; otherwise how many like it were left out before it. */
	function admit(key: string): number | undefined {
		const at = now()
		let flood = floods.get(key)
		if (flood && at - flood.start >= windowMs) {
			flood.start = at
			flood.count = 0
		}
		if (!flood) {
			if (floods.size >= MAX_FLOODS) prune(at)
			flood = { start: at, count: 0, suppressed: 0 }
			floods.set(key, flood)
		}
		if (flood.count >= limit) {
			flood.suppressed++
			return undefined
		}
		flood.count++
		const suppressed = flood.suppressed
		flood.suppressed = 0
		return suppressed
	}

	function prune(at: number): void {
		for (const [key, flood] of floods) if (at - flood.start >= windowMs && flood.suppressed === 0) floods.delete(key)
		if (floods.size >= MAX_FLOODS) floods.clear()
	}

	function deliver(report: RendererErrorReport): void {
		const failed = (error: unknown): void => log('Zing could not report an error to the host', report, error)
		try {
			send(report).catch(failed)
		} catch (error) {
			failed(error)
		}
	}

	function report(kind: RendererErrorReport['kind'], error: unknown, message: string): void {
		if (typeof error === 'object' && error !== null) reported.add(error)
		const suppressed = admit(`${kind}\n${message}`)
		if (suppressed === undefined) return
		const stack = error instanceof Error ? error.stack : undefined
		deliver({
			kind,
			message: suppressed > 0 ? `${message} (and ${suppressed} more like it)` : message,
			...(stack ? { stack } : {}),
			view,
		})
	}

	const isReported = (reason: unknown): boolean => typeof reason === 'object' && reason !== null && reported.has(reason)

	return {
		bridge(channel, error) {
			const message = describe(error)
			report('bridge', error, message.includes(`${channel}:`) ? message : `${channel}: ${message}`)
		},
		error(error, message) {
			if (isReported(error)) return
			report('error', error, error === undefined || error === null ? (message ?? 'Unknown error') : describe(error))
		},
		rejection(reason) {
			if (isReported(reason)) return false
			report('unhandledrejection', reason, describe(reason))
			return true
		},
		isReported,
	}
}

/** A readable message for anything thrown. */
export function describe(error: unknown): string {
	if (error instanceof Error) return error.message ? `${error.name}: ${error.message}` : error.name
	if (typeof error === 'string') return error
	try {
		return JSON.stringify(error) ?? String(error)
	} catch {
		return String(error)
	}
}

/** The view's root name from its page hash: `#overlay-stack` → `overlay-stack`; the window's chrome has none. */
export function viewName(hash: string): string {
	return hash.replace(/^#/, '') || 'chrome'
}

/** The slice of `window` the listeners need. */
export interface ErrorEventTarget {
	addEventListener(type: 'error', listener: (event: { readonly error?: unknown; readonly message?: string }) => void): void
	addEventListener(type: 'unhandledrejection', listener: (event: { readonly reason: unknown; preventDefault(): void }) => void): void
}

/**
 * Sends the view's uncaught errors and unhandled rejections to the reporter.
 * A rejection already reported as a bridge failure is marked handled, so it is
 * neither reported twice nor logged again as unhandled; everything else keeps
 * its default console output.
 */
export function installErrorReporting(target: ErrorEventTarget, reporter: ErrorReporter): void {
	target.addEventListener('error', (event) => reporter.error(event.error, event.message))
	target.addEventListener('unhandledrejection', (event) => {
		if (reporter.isReported(event.reason)) event.preventDefault()
		else reporter.rejection(event.reason)
	})
}

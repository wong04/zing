import type { InvokeChannel } from '../ipc'
import type { Invoke } from '../zingApi'

/** The channel that carries reports; its own failures are never reported through itself. */
export const REPORT_CHANNEL: InvokeChannel = 'app:report-error'

/**
 * Wraps the bridge's invoke so a failed host call is reported once, at the source,
 * instead of at each of the renderer's call sites. The returned promise still
 * rejects with the same error for callers that await it. A call that throws
 * before it has a promise (e.g. `postMessage` failing) becomes a rejection too.
 */
export function reportingInvoke(invoke: Invoke, onFailure: (channel: InvokeChannel, error: unknown) => void): Invoke {
	return ((channel: InvokeChannel, ...args: never[]) => {
		let call: Promise<unknown>
		try {
			call = (invoke as (channel: InvokeChannel, ...args: unknown[]) => Promise<unknown>)(channel, ...args)
		} catch (error) {
			call = Promise.reject(error)
		}
		return call.catch((error: unknown) => {
			if (channel !== REPORT_CHANNEL) {
				try {
					onFailure(channel, error)
				} catch (reportError) {
					// The reporter failed; the caller still gets the original error.
					console.error('Zing could not report a failed host call', channel, reportError)
				}
			}
			throw error
		})
	}) as Invoke
}

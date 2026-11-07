import { useCallback, useEffect, useRef } from 'react'
import type { EngineExit, EngineJobSpec } from '@shared/engines/engineJob'
import { zing } from '../../api'
import { usePanelId } from '../panelId'

/** Exits of other jobs (another panel's, in the same window) that arrived before anyone waited for them. */
const KEEP_EARLY = 20

export interface EngineJob {
	/** Starts the job and resolves with its exit, once. Rejects with the reason the host refused it. */
	run(engineId: string, spec: EngineJobSpec): Promise<EngineExit>
	/** Stops the running job; its outputs are discarded. The exit still arrives. */
	cancel(): Promise<void>
}

/** One engine job at a time for this tool's panel (closing the panel stops it in the host). */
export function useEngineJob(): EngineJob {
	const panelId = usePanelId()
	const waiting = useRef(new Map<string, (exit: EngineExit) => void>())
	const early = useRef(new Map<string, EngineExit>())
	const current = useRef<string | null>(null)
	// The job is asked for but its id has not come back yet; a Cancel pressed meanwhile is applied when it does.
	const starting = useRef(false)
	const cancelWhenStarted = useRef(false)

	useEffect(() => {
		const waits = waiting.current
		const arrived = early.current
		const stop = zing.onEngineExit((exit) => {
			const resolve = waits.get(exit.jobId)
			if (resolve) {
				waits.delete(exit.jobId)
				resolve(exit)
				return
			}
			arrived.set(exit.jobId, exit)
			if (arrived.size > KEEP_EARLY) arrived.delete(arrived.keys().next().value!)
		})
		return stop
	}, [])

	const run = useCallback(
		async (engineId: string, spec: EngineJobSpec): Promise<EngineExit> => {
			starting.current = true
			cancelWhenStarted.current = false
			current.current = null
			let jobId: string
			try {
				jobId = await zing.runEngine(engineId, panelId, spec)
			} finally {
				starting.current = false
			}
			current.current = jobId
			if (cancelWhenStarted.current) await zing.cancelEngineJob(jobId)
			// A short job can end before the answer to runEngine arrives.
			const already = early.current.get(jobId)
			if (already) {
				early.current.delete(jobId)
				return already
			}
			return new Promise<EngineExit>((resolve) => waiting.current.set(jobId, resolve))
		},
		[panelId],
	)

	const cancel = useCallback(async (): Promise<void> => {
		if (starting.current) cancelWhenStarted.current = true
		else if (current.current !== null) await zing.cancelEngineJob(current.current)
	}, [])

	return { run, cancel }
}

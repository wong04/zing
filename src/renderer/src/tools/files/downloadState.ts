import type { EngineExit, EngineOutputLine } from '@shared/engines/engineJob'
import { isFinishingLine, parseProgress } from '@shared/convert/ytdlpArgs'
import { explainMediaFailure, type MediaFailure } from '@shared/convert/ytdlpErrors'

// The Download tool's state. Held in the panel only: nothing about a download outlives the panel but the file itself.

type JobEvent = { readonly type: 'output'; readonly jobId: string; readonly lines: readonly EngineOutputLine[] } | { readonly type: 'exit'; readonly exit: EngineExit }

export type DownloadAction =
	| { readonly type: 'starting' }
	| { readonly type: 'started'; readonly jobId: string }
	| { readonly type: 'start-failed'; readonly message: string }
	| { readonly type: 'page-saved'; readonly name: string }
	| JobEvent

type Phase =
	| { readonly phase: 'idle' }
	/** `jobId` is null until the host has answered; events of any job are kept meanwhile, since another tool's jobs share the window's events. */
	| { readonly phase: 'running'; readonly jobId: string | null; readonly percent: number | null; readonly finishing: boolean; readonly early: readonly JobEvent[] }
	| { readonly phase: 'failed'; readonly failure: MediaFailure }
	| { readonly phase: 'cancelled' }
	| { readonly phase: 'done'; readonly file: string }

/** `saved` lists the files made while this panel was open (downloads and saved pages), by name. */
export type DownloadState = Phase & { readonly saved: readonly string[] }

export const initialDownload: DownloadState = { phase: 'idle', saved: [] }

const noFile = 'yt-dlp finished, but no file was made.'

function exited(state: DownloadState, exit: EngineExit): DownloadState {
	const { saved } = state
	if (exit.cancelled) return { phase: 'cancelled', saved }
	if (exit.error !== undefined) return { phase: 'failed', failure: { message: exit.error, offerUpdate: false }, saved }
	if (exit.code !== 0) return { phase: 'failed', failure: explainMediaFailure(exit.stderrTail.join('\n'), exit.code), saved }
	const file = exit.outputs[0]?.name
	if (file === undefined) return { phase: 'failed', failure: { message: noFile, offerUpdate: false }, saved }
	return { phase: 'done', file, saved: [...saved, file] }
}

function applyEvent(state: DownloadState, event: JobEvent): DownloadState {
	if (state.phase !== 'running') return state
	if (event.type === 'exit') return event.exit.jobId === state.jobId ? exited(state, event.exit) : state
	if (event.jobId !== state.jobId) return state
	let { percent, finishing } = state
	for (const { text } of event.lines) {
		percent = parseProgress(text) ?? percent
		finishing = finishing || isFinishingLine(text)
	}
	return { ...state, percent, finishing }
}

export function downloadReducer(state: DownloadState, action: DownloadAction): DownloadState {
	switch (action.type) {
		case 'starting':
			return state.phase === 'running' ? state : { phase: 'running', jobId: null, percent: null, finishing: false, early: [], saved: state.saved }
		case 'started': {
			if (state.phase !== 'running' || state.jobId !== null) return state
			const known: DownloadState = { ...state, jobId: action.jobId, early: [] }
			return state.early.reduce(applyEvent, known)
		}
		case 'start-failed':
			return { phase: 'failed', failure: { message: action.message, offerUpdate: false }, saved: state.saved }
		case 'page-saved':
			return { ...state, saved: [...state.saved, action.name] }
		case 'output':
		case 'exit':
			if (state.phase === 'running' && state.jobId === null) return { ...state, early: [...state.early, action] }
			return applyEvent(state, action)
	}
}

import { describe, expect, it } from 'vitest'
import type { EngineExit } from '@shared/engines/engineJob'
import { downloadReducer, initialDownload, type DownloadState } from './downloadState'

const exit = (partial: Partial<EngineExit> & { jobId: string }): EngineExit => ({ code: 0, cancelled: false, outputs: [], stderrTail: [], ...partial })
const lines = (...texts: string[]) => texts.map((text) => ({ stream: 'out' as const, text }))
const run = (state: DownloadState, ...actions: Parameters<typeof downloadReducer>[1][]): DownloadState => actions.reduce(downloadReducer, state)

const running = (jobId: string | null = 'job-1'): DownloadState => run(initialDownload, { type: 'starting' }, ...(jobId ? [{ type: 'started' as const, jobId }] : []))

describe('the Download tool state', () => {
	it('starts idle, with nothing kept', () => {
		expect(initialDownload).toEqual({ phase: 'idle', saved: [] })
	})
	it('shows the download percentage from progress lines of its own job', () => {
		const state = run(running(), { type: 'output', jobId: 'job-1', lines: lines('[youtube] Extracting URL', '[download]  42.0% of 10.00MiB at 1MiB/s') })
		expect(state).toMatchObject({ phase: 'running', percent: 42, finishing: false })
	})
	it('says it is finishing once yt-dlp starts converting', () => {
		const state = run(running(), { type: 'output', jobId: 'job-1', lines: lines('[download] 100% of 1MiB', '[ExtractAudio] Destination: audio.mp3') })
		expect(state).toMatchObject({ phase: 'running', percent: 100, finishing: true })
	})
	it('ignores the lines and exit of any other job in the window', () => {
		const state = run(running(), { type: 'output', jobId: 'other', lines: lines('[download]  99.0% of x') }, { type: 'exit', exit: exit({ jobId: 'other' }) })
		expect(state).toMatchObject({ phase: 'running', percent: null })
	})
	it('keeps what arrives before the job id is known, and replays only its own job', () => {
		const state = run(
			running(null),
			{ type: 'output', jobId: 'job-1', lines: lines('[download]  10.0% of x') },
			{ type: 'output', jobId: 'other', lines: lines('[download]  90.0% of x') },
			{ type: 'started', jobId: 'job-1' },
		)
		expect(state).toMatchObject({ phase: 'running', jobId: 'job-1', percent: 10 })
	})
	it('finishes a job whose exit arrived before its id', () => {
		const state = run(running(null), { type: 'exit', exit: exit({ jobId: 'job-1', outputs: [{ name: 'audio.mp3', token: 't' }] }) }, { type: 'started', jobId: 'job-1' })
		expect(state).toEqual({ phase: 'done', file: 'audio.mp3', saved: ['audio.mp3'] })
	})
	it('lists the finished file and keeps earlier ones', () => {
		const first = run(running(), { type: 'exit', exit: exit({ jobId: 'job-1', outputs: [{ name: 'audio.mp3', token: 'a' }] }) })
		const second = run(first, { type: 'starting' }, { type: 'started', jobId: 'job-2' }, { type: 'exit', exit: exit({ jobId: 'job-2', outputs: [{ name: 'audio 2.mp3', token: 'b' }] }) })
		expect(second).toEqual({ phase: 'done', file: 'audio 2.mp3', saved: ['audio.mp3', 'audio 2.mp3'] })
	})
	it('reports a cancel as cancelled, with no file', () => {
		expect(run(running(), { type: 'exit', exit: exit({ jobId: 'job-1', code: 1, cancelled: true }) })).toEqual({ phase: 'cancelled', saved: [] })
	})
	it('keeps the file of a job that was cancelled while it was being placed', () => {
		expect(run(running(), { type: 'exit', exit: exit({ jobId: 'job-1', outputs: [{ name: 'video.mp4', token: 't' }] }) })).toMatchObject({ phase: 'done', file: 'video.mp4' })
	})
	it('explains a failure from the stderr tail, and offers the update only when it would help', () => {
		const failed = run(running(), { type: 'exit', exit: exit({ jobId: 'job-1', code: 1, stderrTail: ['ERROR: unable to download video data: HTTP Error 403: Forbidden'] }) })
		expect(failed).toMatchObject({ phase: 'failed', failure: { offerUpdate: true } })
		const bot = run(running(), { type: 'exit', exit: exit({ jobId: 'job-1', code: 1, stderrTail: ["ERROR: [youtube] x: Sign in to confirm you're not a bot"] }) })
		expect(bot).toMatchObject({ phase: 'failed', failure: { offerUpdate: false } })
	})
	it('reports the runner error when the file could not be placed', () => {
		const failed = run(running(), { type: 'exit', exit: exit({ jobId: 'job-1', code: 0, error: "Couldn't place audio.mp3" }) })
		expect(failed).toMatchObject({ phase: 'failed', failure: { message: "Couldn't place audio.mp3", offerUpdate: false } })
	})
	it('reports a clean exit with no file as a failure rather than a success', () => {
		expect(run(running(), { type: 'exit', exit: exit({ jobId: 'job-1' }) })).toMatchObject({ phase: 'failed' })
	})
	it('reports a job that could not start', () => {
		const state = run(initialDownload, { type: 'starting' }, { type: 'start-failed', message: 'FFmpeg has not been downloaded yet' })
		expect(state).toMatchObject({ phase: 'failed', failure: { message: 'FFmpeg has not been downloaded yet', offerUpdate: false } })
	})
	it('remembers a saved page in the same list', () => {
		expect(run(initialDownload, { type: 'page-saved', name: 'A page.html' })).toEqual({ phase: 'idle', saved: ['A page.html'] })
	})
	it('lets a new download start from a failure or a finish, but not twice at once', () => {
		const failed = run(running(), { type: 'exit', exit: exit({ jobId: 'job-1', code: 1, stderrTail: ['x'] }) })
		expect(run(failed, { type: 'starting' }).phase).toBe('running')
		expect(run(running(), { type: 'starting' })).toEqual(running())
	})
})

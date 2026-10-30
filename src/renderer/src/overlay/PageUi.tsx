import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { EMPTY_PAGE_UI, hasVisiblePageUi, type DownloadEntry, type PageUiState, type PermissionKind } from '@shared/browser/pageUi'
import { zing } from '../api'
import './pageui.css'

function usePageUiState(): PageUiState {
	const [state, setState] = useState<PageUiState>(EMPTY_PAGE_UI)
	useEffect(() => {
		let pushed = false
		const off = zing.onPageUiState((next) => {
			pushed = true
			setState(next)
		})
		void zing.getPageUi().then((initial) => {
			if (!pushed) setState(initial)
		})
		return off
	}, [])
	return state
}

const SIZE_UNITS = ['B', 'KB', 'MB', 'GB']

function formatBytes(bytes: number): string {
	let value = bytes
	let unit = 0
	while (value >= 1024 && unit < SIZE_UNITS.length - 1) {
		value /= 1024
		unit++
	}
	return `${unit === 0 ? value : value.toFixed(1)} ${SIZE_UNITS[unit]}`
}

function describeDownload(d: DownloadEntry): string {
	if (d.status === 'progressing') return d.total > 0 ? `${formatBytes(d.received)} of ${formatBytes(d.total)}` : formatBytes(d.received)
	if (d.status === 'completed') return d.error ?? formatBytes(d.received)
	return d.status === 'cancelled' ? 'Cancelled' : 'Failed'
}

const PERMISSION_VERB: Record<PermissionKind, string> = {
	camera: 'use your camera',
	microphone: 'use your microphone',
	'camera and microphone': 'use your camera and microphone',
	location: 'know your location',
	notifications: 'show notifications',
}

function FindBar({ state }: { state: PageUiState }) {
	const input = useRef<HTMLInputElement>(null)
	const find = state.find
	const [text, setText] = useState(find?.query ?? '')
	const open = find !== undefined
	useEffect(() => {
		input.current?.focus()
		input.current?.select()
	}, [state.findFocus, open])
	if (!find) return null

	const onKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
		if (e.key === 'Escape') void zing.closeFind()
		if (e.key === 'Enter') void zing.stepFind(e.shiftKey ? 'previous' : 'next')
	}
	const count = find.query === '' ? '' : find.matches === 0 ? 'No matches' : `${find.active} of ${find.matches}`
	return (
		<div className="pageui-card" role="search" aria-label="Find in page">
			<div className="pageui-row">
				<input
					ref={input}
					className="pageui-input"
					type="text"
					aria-label="Find in page"
					placeholder="Find in page"
					value={text}
					onChange={(e) => {
						setText(e.target.value)
						void zing.updateFind(e.target.value)
					}}
					onKeyDown={onKeyDown}
				/>
				<span className="pageui-count" aria-live="polite">
					{count}
				</span>
				<button type="button" className="pageui-button icon" aria-label="Previous match" disabled={find.matches === 0} onClick={() => void zing.stepFind('previous')}>
					↑
				</button>
				<button type="button" className="pageui-button icon" aria-label="Next match" disabled={find.matches === 0} onClick={() => void zing.stepFind('next')}>
					↓
				</button>
				<button type="button" className="pageui-button icon" aria-label="Close find" onClick={() => void zing.closeFind()}>
					×
				</button>
			</div>
		</div>
	)
}

function DownloadRow({ download }: { download: DownloadEntry }) {
	const fraction = download.total > 0 ? Math.min(1, download.received / download.total) : 0
	return (
		<li className="download">
			<span className="download-name" title={download.filename}>
				{download.filename}
			</span>
			{download.status === 'progressing' && (
				<div
					className="download-bar"
					role="progressbar"
					aria-label={`Downloading ${download.filename}`}
					aria-valuemin={0}
					aria-valuemax={100}
					aria-valuenow={Math.round(fraction * 100)}
				>
					<span style={{ width: `${fraction * 100}%` }} />
				</div>
			)}
			<span className={download.status === 'failed' ? 'download-status failed' : 'download-status'}>{describeDownload(download)}</span>
			<div className="pageui-row">
				{download.status === 'progressing' && (
					<button type="button" className="pageui-button" onClick={() => void zing.cancelDownload(download.id)}>
						Cancel
					</button>
				)}
				{download.status === 'completed' && (
					<>
						<button type="button" className="pageui-button" onClick={() => void zing.openDownload(download.id)}>
							Open
						</button>
						<button type="button" className="pageui-button" onClick={() => void zing.showDownload(download.id)}>
							Show in folder
						</button>
					</>
				)}
			</div>
		</li>
	)
}

function Downloads({ state }: { state: PageUiState }) {
	if (!state.downloadsOpen || state.downloads.length === 0) return null
	return (
		<section className="pageui-card" aria-label="Downloads">
			<div className="pageui-row">
				<span className="pageui-title">Downloads</span>
				<button type="button" className="pageui-button" onClick={() => void zing.clearDownloads()}>
					Clear
				</button>
				<button type="button" className="pageui-button icon" aria-label="Close downloads" onClick={() => void zing.dismissDownloads()}>
					×
				</button>
			</div>
			<ul className="download-list">
				{[...state.downloads].reverse().map((d) => (
					<DownloadRow key={d.id} download={d} />
				))}
			</ul>
		</section>
	)
}

function PermissionPrompt({ state }: { state: PageUiState }) {
	const prompt = state.permission
	if (!prompt) return null
	return (
		<div className="pageui-card" role="alertdialog" aria-label="Permission request">
			<span>
				<strong>{prompt.origin}</strong> wants to {PERMISSION_VERB[prompt.kind]}
			</span>
			<div className="pageui-row">
				<button type="button" className="pageui-button primary" onClick={() => void zing.allowPermission(prompt.id)}>
					Allow
				</button>
				<button type="button" className="pageui-button" onClick={() => void zing.denyPermission(prompt.id)}>
					Deny
				</button>
			</div>
		</div>
	)
}

/** Root of the page-UI view: a column of cards main sizes the view to. */
export function PageUi() {
	const state = usePageUiState()
	const root = useRef<HTMLDivElement>(null)
	const visible = hasVisiblePageUi(state)
	useLayoutEffect(() => {
		const element = root.current
		if (!element || !visible) {
			void zing.setPageUiHeight(0)
			return
		}
		const report = (): void => void zing.setPageUiHeight(element.getBoundingClientRect().height)
		report()
		const observer = new ResizeObserver(report)
		observer.observe(element)
		return () => observer.disconnect()
	}, [visible])
	if (!visible) return null
	return (
		<div ref={root} className="pageui">
			<PermissionPrompt state={state} />
			<FindBar state={state} />
			<Downloads state={state} />
		</div>
	)
}

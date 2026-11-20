import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { EMPTY_PAGE_UI, hasVisiblePageUi, type DownloadEntry, type PageUiState, type PermissionKind, type SaveAnswer, type ScriptDialogPrompt } from '@shared/browser/pageUi'
import { formatBytes } from '@shared/format/formatBytes'
import { followHostState } from '../hostState'
import { zing } from '../api'
import { useAccentTheme } from '../settings/useAccentTheme'
import './pageui.css'

function usePageUiState(): PageUiState {
	const [state, setState] = useState<PageUiState>(EMPTY_PAGE_UI)
	useEffect(() => {
		return followHostState(zing.onPageUiState, zing.getPageUi, setState)
	}, [])
	return state
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
	clipboard: 'see text and images you copy',
	'multiple downloads': 'download multiple files',
	files: 'edit the files or folders you picked',
	'pop-ups': 'open pop-ups',
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

/** As in Chromium: a click meant for the page (a double-click's second half) can't land on a prompt that just opened under it. */
const ALLOW_ARMS_AFTER_MS = 500

function PermissionPrompt({ state }: { state: PageUiState }) {
	const prompt = state.permission
	if (!prompt) return null
	// Keyed by id, so each new prompt starts with Allow unarmed.
	return prompt.kind === 'pop-ups' ? <PopupCard key={prompt.id} prompt={prompt} /> : <PermissionCard key={prompt.id} prompt={prompt} />
}

function useArmed(): boolean {
	const [armed, setArmed] = useState(false)
	useEffect(() => {
		const timer = setTimeout(() => setArmed(true), ALLOW_ARMS_AFTER_MS)
		return () => clearTimeout(timer)
	}, [])
	return armed
}

function PermissionCard({ prompt }: { prompt: NonNullable<PageUiState['permission']> }) {
	const armed = useArmed()
	// Only a web site's notifications can be remembered; everything else lasts as long as the page.
	const rememberable = prompt.kind === 'notifications' && prompt.origin.startsWith('http')
	const [remember, setRemember] = useState(false)
	const allow = (): void => void (remember && rememberable ? zing.rememberPermission(prompt.id) : zing.allowPermission(prompt.id))
	return (
		<div className="pageui-card" role="alertdialog" aria-label="Permission request">
			<span>
				<strong>{prompt.origin}</strong> wants to {PERMISSION_VERB[prompt.kind]}
			</span>
			{prompt.tab !== undefined && <span className="pageui-detail">From the tab “{prompt.tab}”</span>}
			{rememberable && (
				<label className="pageui-detail pageui-remember">
					<input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
					Remember for this site
				</label>
			)}
			<div className="pageui-row">
				<button type="button" className="pageui-button primary" disabled={!armed} onClick={allow}>
					Allow
				</button>
				<button type="button" className="pageui-button" onClick={() => void zing.denyPermission(prompt.id)}>
					Deny
				</button>
			</div>
		</div>
	)
}

/** A pop-up the page opened without a click, already blocked. Only a web site (an http origin) can be allowed for the session. */
function PopupCard({ prompt }: { prompt: NonNullable<PageUiState['permission']> }) {
	const armed = useArmed()
	return (
		<div className="pageui-card" role="alertdialog" aria-label="Pop-up blocked">
			<span>
				Zing blocked a pop-up from <strong>{prompt.origin}</strong>
			</span>
			{prompt.tab !== undefined && <span className="pageui-detail">From the tab “{prompt.tab}”</span>}
			<div className="pageui-row">
				<button type="button" className="pageui-button primary" disabled={!armed} onClick={() => void zing.allowPermission(prompt.id)}>
					Allow once
				</button>
				{prompt.origin.startsWith('http') && (
					<button type="button" className="pageui-button" disabled={!armed} onClick={() => void zing.allowPopupsForSite(prompt.id)}>
						Allow for this site, this session
					</button>
				)}
				<button type="button" className="pageui-button" onClick={() => void zing.denyPermission(prompt.id)}>
					Keep blocked
				</button>
			</div>
		</div>
	)
}

/** Who a page dialog names: its origin, or "This page" for one with no web origin. */
function dialogSource(prompt: ScriptDialogPrompt): string {
	return prompt.origin === 'null' || prompt.origin === 'this page' ? 'This page' : prompt.origin
}

/** A page's alert, confirm or prompt, or its "Leave site?" as a tab or window closes. Enter answers OK, Esc Cancel. */
function ScriptDialog({ state }: { state: PageUiState }) {
	const prompt = state.dialog
	if (!prompt) return null
	// Keyed by id, so each new dialog starts with its own text and takes the focus.
	return <ScriptDialogCard key={prompt.id} prompt={prompt} />
}

export function ScriptDialogCard({ prompt }: { prompt: ScriptDialogPrompt }) {
	const [text, setText] = useState(prompt.defaultText ?? '')
	const leaving = prompt.kind === 'beforeunload'
	const accept = (): void => void zing.answerDialog(prompt.id, true, prompt.kind === 'prompt' ? text : undefined)
	const dismiss = (): void => void zing.answerDialog(prompt.id, false)
	const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
		if (e.key === 'Escape') dismiss()
		if (e.key === 'Enter' && prompt.kind === 'prompt') accept()
	}
	return (
		<div className="pageui-card" role="alertdialog" aria-label={leaving ? 'Leave site?' : `${dialogSource(prompt)} says`} onKeyDown={onKeyDown}>
			{leaving ? (
				<span>
					<strong>Leave site?</strong> Changes you made on <strong>{dialogSource(prompt)}</strong> may not be saved.
				</span>
			) : (
				<>
					<span className="pageui-title">{dialogSource(prompt)} says</span>
					{prompt.message !== '' && <span className="pageui-message">{prompt.message}</span>}
				</>
			)}
			{prompt.tab !== undefined && <span className="pageui-detail">From the tab “{prompt.tab}”</span>}
			{prompt.kind === 'prompt' && (
				<input className="pageui-input" type="text" aria-label="Answer" value={text} autoFocus onChange={(e) => setText(e.target.value)} />
			)}
			<div className="pageui-row">
				<button type="button" className="pageui-button primary" autoFocus={prompt.kind !== 'prompt'} onClick={accept}>
					{leaving ? 'Leave' : 'OK'}
				</button>
				{prompt.kind !== 'alert' && (
					<button type="button" className="pageui-button" onClick={dismiss}>
						{leaving ? 'Stay' : 'Cancel'}
					</button>
				)}
			</div>
		</div>
	)
}

/** The vault's offer to save a sign-in or update a saved login: the origin and username only, never the password. */
function SaveLoginPrompt({ state }: { state: PageUiState }) {
	const prompt = state.saveLogin
	if (!prompt) return null
	const update = prompt.kind === 'update'
	const answer = (choice: SaveAnswer) => () => void zing.answerSaveLogin(prompt.pageId, choice)
	return (
		<div className="pageui-card" role="alertdialog" aria-label={update ? 'Update saved password' : 'Save password'}>
			<span>
				{update ? 'Update the password for ' : 'Save the password for '}
				<strong>{prompt.replaces ?? prompt.username}</strong> on <strong>{prompt.origin}</strong>?
			</span>
			<div className="pageui-row">
				<button type="button" className="pageui-button primary" onClick={answer('save')}>
					{update ? 'Update' : 'Save'}
				</button>
				<button type="button" className="pageui-button" onClick={answer('not-now')}>
					Not now
				</button>
				<button type="button" className="pageui-button" onClick={answer('never')}>
					Never for this site
				</button>
			</div>
		</div>
	)
}

/** Root of the page-UI view: a column of cards main sizes the view to. */
export function PageUi() {
	useAccentTheme()
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
			<ScriptDialog state={state} />
			<PermissionPrompt state={state} />
			<SaveLoginPrompt state={state} />
			<FindBar state={state} />
			<Downloads state={state} />
		</div>
	)
}

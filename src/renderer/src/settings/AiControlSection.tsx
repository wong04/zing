import { useEffect, useState } from 'react'
import type { AiSettingsState, AiSiteLevel } from '@shared/ipc'
import { zing } from '../api'
import { PillButton, PillField, PillMenu } from '../components'
import { SettingRowShell } from './SettingRowShell'
import { setAiSiteRulesKept, setAiTokenKept } from './privacy'
import type { SettingsHandle } from './useSettings'

const LEVELS: readonly { value: AiSiteLevel; label: string }[] = [
	{ value: 'AskSubmit', label: 'Ask before submit, buy or send' },
	{ value: 'AskAll', label: 'Ask before every action' },
	{ value: 'Never', label: 'Don’t ask' },
]

const MASK = '•'.repeat(16)
const MIN_PORT = 1024
const MAX_PORT = 65535

/** The token in `text` hidden unless `shown`: the snippets carry it, and the sheet may be on a shared screen. */
export function masked(text: string, token: string | undefined, shown: boolean): string {
	return shown || !token ? text : text.split(token).join(MASK)
}

/** A port typed in, or null when it isn't a whole number Zing may listen on. */
export function parsePort(text: string): number | null {
	if (!/^\d+$/.test(text.trim())) return null
	const port = Number(text)
	return port >= MIN_PORT && port <= MAX_PORT ? port : null
}

/** What the host has for Settings, fetched again on every AI change (ai:state) and when the switch or the port change. */
function useAiSettings(enabled: boolean, port: number): AiSettingsState | null {
	const [state, setState] = useState<AiSettingsState | null>(null)
	useEffect(() => {
		let live = true
		const fetch = (): void =>
			void zing.getAiSettings().then((next) => {
				if (!live) return
				setAiTokenKept(next.tokenStored)
				setAiSiteRulesKept(next.sites.length > 0)
				setState(next)
			})
		fetch()
		const stop = zing.onAiState(fetch)
		return () => {
			live = false
			stop()
		}
	}, [enabled, port])
	return state
}

function PortRow({ handle }: { handle: SettingsHandle }) {
	const saved = handle.settings.aiControlPort
	const [text, setText] = useState(String(saved))
	const port = parsePort(text)
	const commit = (): void => {
		if (port === null) setText(String(saved))
		else if (port !== saved) handle.set('aiControlPort', port)
	}
	return (
		<SettingRowShell title="Port" detail={port === null ? `A whole number from ${MIN_PORT} to ${MAX_PORT}.` : 'On 127.0.0.1 only. Change it if another app uses this one.'} warning={port === null}>
			<div style={{ width: 96 }}>
				<PillField aria-label="AI control port" inputMode="numeric" value={text} onChange={setText} onSubmit={commit} onBlur={commit} />
			</div>
		</SettingRowShell>
	)
}

interface SnippetProps {
	readonly title: string
	readonly detail: string
	readonly text: string
	readonly shown: boolean
	readonly token: string | undefined
	readonly copy: 'claude-code' | 'claude-desktop'
}

function Snippet({ title, detail, text, shown, token, copy }: SnippetProps) {
	return (
		<>
			<SettingRowShell title={title} detail={detail}>
				<PillButton variant="outlined" size="small" aria-label={`Copy the ${title} setup`} onClick={() => void zing.copyAi(copy)}>
					Copy
				</PillButton>
			</SettingRowShell>
			<pre className="ai-snippet" aria-label={`${title} setup`}>
				{masked(text, token, shown)}
			</pre>
		</>
	)
}

/** AI control's Settings: the port, the token, the Connect Claude snippets, the per-site levels and Stop all. */
export function AiControlSection({ handle }: { handle: SettingsHandle }) {
	const enabled = handle.settings.aiControlEnabled
	const state = useAiSettings(enabled, handle.settings.aiControlPort)
	const [shown, setShown] = useState(false)
	if (!state) return null
	if (!state.available) return <SettingRowShell title="AI control is unavailable" detail="Zing couldn’t read your AI site settings this launch. The file was left as it is." warning />
	return (
		<>
			{enabled ? (
				<>
					{/* Keyed by the saved port, so the field starts over from it once it changes. */}
					<PortRow key={handle.settings.aiControlPort} handle={handle} />
					{state.error ? <SettingRowShell title="AI control isn’t reachable" detail={state.error} warning /> : null}
					<SettingRowShell title="Access token" detail="Clients that connect over HTTP need it. Regenerating it cuts off every client using the old one.">
						<div className="ai-token-actions">
							<PillButton variant="ghost" size="small" onClick={() => setShown(!shown)}>
								{shown ? 'Hide' : 'Show'}
							</PillButton>
							<PillButton variant="outlined" size="small" aria-label="Copy the access token" onClick={() => void zing.copyAi('token')}>
								Copy
							</PillButton>
							<PillButton variant="outlined" size="small" onClick={() => void zing.regenerateAiToken()}>
								Regenerate
							</PillButton>
						</div>
					</SettingRowShell>
					<code className="ai-token" aria-label="Access token">
						{shown ? state.token : MASK}
					</code>
					{state.claudeCode ? <Snippet title="Connect Claude Code" detail="Run this once in a terminal." text={state.claudeCode} shown={shown} token={state.token} copy="claude-code" /> : null}
					{state.claudeDesktop ? (
						<Snippet
							title="Connect the Claude desktop app"
							detail="Add this to claude_desktop_config.json. It runs zing-mcp.exe, next to Zing.exe, which needs no token."
							text={state.claudeDesktop}
							shown={shown}
							token={state.token}
							copy="claude-desktop"
						/>
					) : null}
				</>
			) : null}
			<SettingRowShell
				title="Shared tabs"
				detail={state.sharedCount === 0 ? 'No tab is shared. Share one from its tab menu.' : `${state.sharedCount} ${state.sharedCount === 1 ? 'tab is' : 'tabs are'} shared.`}
			>
				<PillButton variant="outlined" size="small" disabled={state.sharedCount === 0} onClick={() => void zing.stopAllAi()}>
					Stop all
				</PillButton>
			</SettingRowShell>
			<SettingRowShell
				title="Sites"
				detail={
					state.sites.length === 0
						? 'Every site asks before a submit, a purchase or a send. Always allow on a confirm adds a site here.'
						: 'These sites ask differently. Remove one to put it back to asking before a submit.'
				}
			/>
			{state.sites.length > 0 && (
				<ul className="shortcut-list" aria-label="AI confirmation levels by site">
					{state.sites.map(({ site, level }) => (
						<li key={site} className="shortcut-row">
							<span className="shortcut-text">
								<strong>{site}</strong>
							</span>
							<PillMenu aria-label={`AI level for ${site}`} options={LEVELS} value={level} onChange={(next) => void zing.setAiSiteLevel(site, next)} />
							<PillButton variant="ghost" size="small" aria-label={`Remove ${site}`} onClick={() => void zing.setAiSiteLevel(site, 'AskSubmit')}>
								Remove
							</PillButton>
						</li>
					))}
				</ul>
			)}
		</>
	)
}

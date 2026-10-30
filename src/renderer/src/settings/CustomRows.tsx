import { useState } from 'react'
import { defaults as defaultShortcuts, limit as shortcutLimit, make as makeShortcut } from '@shared/browser/startShortcuts'
import { allInputs } from '@shared/convert/formatCatalog'
import { MAX_SLEEP_MINUTES } from '@shared/session/sleepSettings'
import { ACCENTS } from '@shared/settings'
import { accentLabel, isAccentTheme, swatchHex } from '@shared/theme/accent'
import { allItems } from '@shared/toolbox/catalog'
import { zing } from '../api'
import { InfoBanner, PillButton, PillField, PillSlider, Toggle } from '../components'
import { ShieldAlertIcon, ShieldCheckIcon } from '../components/icons'
import { AccountPane } from '../account/AccountPane'
import { privacyStatus } from './privacy'
import type { CustomId } from './rows'
import { SettingRowShell } from './SettingRowShell'
import type { SettingsHandle } from './useSettings'

function AccentPicker({ handle }: { handle: SettingsHandle }) {
	return (
		<SettingRowShell title="Accent colour">
			<div className="swatches" role="radiogroup" aria-label="Accent colour">
				{ACCENTS.map((accent) => {
					const selected = handle.settings.accent === accent
					const label = isAccentTheme(accent) ? accentLabel(accent) : accent
					return (
						<button
							key={accent}
							type="button"
							role="radio"
							aria-checked={selected}
							aria-label={label}
							title={label}
							tabIndex={selected ? 0 : -1}
							className={selected ? 'swatch selected' : 'swatch'}
							onClick={() => handle.set('accent', accent)}
						>
							<span style={{ background: isAccentTheme(accent) ? swatchHex(accent) : undefined }} />
						</button>
					)
				})}
			</div>
		</SettingRowShell>
	)
}

function DefaultBrowserRow() {
	return (
		<SettingRowShell title="Default browser" detail="Opens Windows' Default apps page, where you choose Zing for links.">
			<PillButton variant="outlined" size="small" onClick={() => void zing.openDefaultAppsSettings()}>
				Make Default
			</PillButton>
		</SettingRowShell>
	)
}

function ShortcutEditor({ handle }: { handle: SettingsHandle }) {
	const stored = handle.settings.startShortcuts
	const list = stored ?? defaultShortcuts
	const [name, setName] = useState('')
	const [address, setAddress] = useState('')
	const candidate = makeShortcut(name, address)
	const duplicate = candidate ? list.some((s) => s.url === candidate.url) : false
	const full = list.length >= shortcutLimit
	const save = (next: typeof list): void => handle.set('startShortcuts', next.map(({ title, url }) => ({ title, url })))

	const add = (): void => {
		if (!candidate || duplicate || full) return
		save([...list, candidate])
		setName('')
		setAddress('')
	}
	const move = (index: number, delta: number): void => {
		const next = [...list]
		const [item] = next.splice(index, 1)
		if (!item) return
		next.splice(index + delta, 0, item)
		save(next)
	}

	return (
		<>
			<SettingRowShell title="Shortcuts" detail="The sites on the new-tab page.">
				<PillButton variant="ghost" size="small" disabled={stored === null} onClick={() => handle.set('startShortcuts', null)}>
					Restore Defaults
				</PillButton>
			</SettingRowShell>
			<ul className="shortcut-list">
				{list.map((shortcut, index) => (
					<li key={shortcut.url} className="shortcut-row">
						<span className="shortcut-text">
							<strong>{shortcut.title}</strong>
							<small>{shortcut.url}</small>
						</span>
						<PillButton variant="ghost" size="small" aria-label={`Move ${shortcut.title} up`} disabled={index === 0} onClick={() => move(index, -1)}>
							Up
						</PillButton>
						<PillButton variant="ghost" size="small" aria-label={`Move ${shortcut.title} down`} disabled={index === list.length - 1} onClick={() => move(index, 1)}>
							Down
						</PillButton>
						<PillButton variant="ghost" size="small" aria-label={`Remove ${shortcut.title}`} onClick={() => save(list.filter((_, i) => i !== index))}>
							Remove
						</PillButton>
					</li>
				))}
			</ul>
			<div className="shortcut-add">
				<PillField value={name} onChange={setName} placeholder="Name (optional)" aria-label="New shortcut name" />
				<PillField value={address} onChange={setAddress} onSubmit={add} placeholder="Address" aria-label="New shortcut address" />
				<PillButton size="small" disabled={!candidate || duplicate || full} onClick={add}>
					Add
				</PillButton>
			</div>
			{full ? <InfoBanner kind="info">That's the most the new-tab page holds ({shortcutLimit}).</InfoBanner> : null}
			{duplicate ? <InfoBanner kind="info">That address is already there.</InfoBanner> : null}
		</>
	)
}

function SleepingTabsRows({ handle }: { handle: SettingsHandle }) {
	const sleep = handle.settings.sleepingTabs
	const update = (patch: Partial<typeof sleep>): void => handle.set('sleepingTabs', { ...sleep, ...patch })
	return (
		<>
			<SettingRowShell title="Put unused tabs to sleep" detail="A sleeping tab keeps its place and wakes when you click it, freeing its memory meanwhile.">
				<Toggle aria-label="Put unused tabs to sleep" checked={sleep.enabled} onChange={(enabled) => update({ enabled })} />
			</SettingRowShell>
			{sleep.enabled ? (
				<>
					<SettingRowShell title={`After ${sleep.afterMinutes} minutes`}>
						<div className="control-slider">
							<PillSlider
								aria-label="Minutes before a tab sleeps"
								min={5}
								max={Math.min(240, MAX_SLEEP_MINUTES)}
								step={5}
								value={Math.min(Math.max(sleep.afterMinutes, 5), 240)}
								valueText={`${sleep.afterMinutes} minutes`}
								onChange={(afterMinutes) => update({ afterMinutes })}
							/>
						</div>
					</SettingRowShell>
					<SettingRowShell title="Also sleep pinned tabs">
						<Toggle aria-label="Also sleep pinned tabs" checked={sleep.sleepPinned} onChange={(sleepPinned) => update({ sleepPinned })} />
					</SettingRowShell>
				</>
			) : null}
		</>
	)
}

function PrivacyBanner({ handle }: { handle: SettingsHandle }) {
	const status = privacyStatus(handle.settings)
	return (
		<div className={status.traceless ? 'privacy-banner ok' : 'privacy-banner warn'} role={status.traceless ? 'status' : 'alert'} data-traceless={status.traceless}>
			{status.traceless ? <ShieldCheckIcon size={24} /> : <ShieldAlertIcon size={24} />}
			<div className="privacy-text">
				<div className="privacy-headline">{status.headline}</div>
				<div className="privacy-detail">{status.detail}</div>
			</div>
		</div>
	)
}

function ClearSessionRow() {
	const [cleared, setCleared] = useState(false)
	return (
		<SettingRowShell title="Clear browsing session" detail={cleared ? 'Cleared.' : 'Signs you out of everything now.'}>
			<PillButton
				variant="outlined"
				size="small"
				onClick={() => {
					void zing.clearBrowsingSession().then(() => setCleared(true))
				}}
			>
				Clear Now
			</PillButton>
		</SettingRowShell>
	)
}

function RecentToolsRow({ handle }: { handle: SettingsHandle }) {
	const count = handle.settings.recentFeatures.length
	return (
		<SettingRowShell title="Recent tools" detail={count === 0 ? 'Nothing listed in the sidebar.' : `${count} listed in the sidebar. Only tool names are kept.`}>
			<PillButton variant="outlined" size="small" disabled={count === 0} onClick={() => handle.set('recentFeatures', [])}>
				Clear
			</PillButton>
		</SettingRowShell>
	)
}

function About({ version }: { version: string }) {
	return (
		<>
			<div className="about-head">
				<div className="auth-mark small" aria-hidden="true">
					Z
				</div>
				<div>
					<div className="about-name">Zing</div>
					<div className="about-version">Version {version}</div>
				</div>
			</div>
			<SettingRowShell title="File formats">
				<span className="row-value">{allInputs().size}</span>
			</SettingRowShell>
			<SettingRowShell title="Tools">
				<span className="row-value">{allItems().length}</span>
			</SettingRowShell>
		</>
	)
}

export function CustomRow({ id, handle, version }: { id: CustomId; handle: SettingsHandle; version: string }) {
	switch (id) {
		case 'accent':
			return <AccentPicker handle={handle} />
		case 'default-browser':
			return <DefaultBrowserRow />
		case 'shortcuts':
			return <ShortcutEditor handle={handle} />
		case 'sleeping-tabs':
			return <SleepingTabsRows handle={handle} />
		case 'privacy-banner':
			return <PrivacyBanner handle={handle} />
		case 'clear-session':
			return <ClearSessionRow />
		case 'recent-tools':
			return <RecentToolsRow handle={handle} />
		case 'account':
			return <AccountPane />
		case 'about':
			return <About version={version} />
	}
}

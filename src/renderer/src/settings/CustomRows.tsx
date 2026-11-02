import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { defaults as defaultShortcuts, limit as shortcutLimit, make as makeShortcut } from '@shared/browser/startShortcuts'
import { allInputs } from '@shared/convert/formatCatalog'
import { MAX_SLEEP_MINUTES } from '@shared/session/sleepSettings'
import { accentLabel, accentThemes, CUSTOM_ACCENT, swatchHex } from '@shared/theme/accent'
import { allItems } from '@shared/toolbox/catalog'
import { zing } from '../api'
import { InfoBanner, PillButton, PillField, PillSlider, Toggle } from '../components'
import { ShieldAlertIcon, ShieldCheckIcon } from '../components/icons'
import { AccountPane } from '../account/AccountPane'
import { privacyStatus } from './privacy'
import type { CustomId } from './rows'
import { SettingRowShell } from './SettingRowShell'
import type { SettingsHandle } from './useSettings'

function AccentSwatch({ label, hex, selected, onSelect }: { label: string; hex: string; selected: boolean; onSelect: () => void }) {
	return (
		<button
			type="button"
			role="radio"
			aria-checked={selected}
			aria-label={label}
			title={label}
			tabIndex={selected ? 0 : -1}
			className={selected ? 'swatch selected' : 'swatch'}
			onClick={onSelect}
		>
			<span style={{ background: hex }} />
		</button>
	)
}

/** Commits on `change`, not `input`: React's onChange also fires on every step of a drag in the colour popup. */
function CustomColourInput({ handle }: { handle: SettingsHandle }) {
	const field = useRef<HTMLInputElement>(null)
	const { set } = handle
	useEffect(() => {
		const input = field.current
		if (!input) throw new Error('The custom colour input is not mounted')
		const commit = (): void => set('customColor', input.value)
		input.addEventListener('change', commit)
		return () => input.removeEventListener('change', commit)
	}, [set])
	return <input ref={field} type="color" className="custom-colour" aria-label="Custom colour" defaultValue={handle.settings.customColor} />
}

function AccentPicker({ handle }: { handle: SettingsHandle }) {
	const { accent, customColor } = handle.settings
	const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
		const delta = e.key === 'ArrowRight' || e.key === 'ArrowDown' ? 1 : e.key === 'ArrowLeft' || e.key === 'ArrowUp' ? -1 : 0
		if (delta === 0) return
		e.preventDefault()
		const themes: (typeof accent)[] = [...accentThemes, CUSTOM_ACCENT]
		const next = (themes.indexOf(accent) + delta + themes.length) % themes.length
		handle.set('accent', themes[next]!)
		;(e.currentTarget.children[next] as HTMLElement).focus()
	}
	return (
		<SettingRowShell title="Theme">
			<div className="swatches" role="radiogroup" aria-label="Theme" onKeyDown={onKeyDown}>
				{accentThemes.map((theme) => (
					<AccentSwatch
						key={theme}
						label={accentLabel(theme)}
						hex={swatchHex(theme)}
						selected={accent === theme}
						onSelect={() => handle.set('accent', theme)}
					/>
				))}
				<AccentSwatch label="Custom" hex={customColor} selected={accent === CUSTOM_ACCENT} onSelect={() => handle.set('accent', CUSTOM_ACCENT)} />
			</div>
			{accent === CUSTOM_ACCENT ? <CustomColourInput handle={handle} /> : null}
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

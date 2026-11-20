import { useEffect, useState } from 'react'
import type { ExtensionRow, ExtensionsState } from '@shared/ipc'
import type { Settings } from '@shared/settings'
import { zing } from '../api'
import { PillButton, Toggle } from '../components'
import { SettingRowShell } from './SettingRowShell'
import type { SettingsHandle } from './useSettings'
import { extensionDetail, loadUnpackedDetail } from './extensionsText'

/** Shown instead of the switch while Stay signed in is off: extensions do nothing in the in-memory profile. */
export function ExtensionsGate({ handle }: { handle: SettingsHandle }) {
	return (
		<SettingRowShell
			title="Extensions need Stay signed in to sites"
			detail="Extensions only run in a profile Zing keeps on disk. Turning on Stay signed in keeps your sign-ins, cookies and site data between launches. Relaunch to apply."
			warning
		>
			<PillButton variant="outlined" size="small" onClick={() => handle.set('persistentProfile', true)}>
				Turn On
			</PillButton>
		</SettingRowShell>
	)
}

const canLoad = (s: Settings): boolean => s.extensions && s.persistentProfile

export function ExtensionsList({ handle }: { handle: SettingsHandle }) {
	const [state, setState] = useState<ExtensionsState | null>(null)
	const [error, setError] = useState<string | null>(null)
	const { settings } = handle

	useEffect(() => {
		let live = true
		zing.getExtensions().then(
			(next) => live && setState(next),
			(e: unknown) => live && setError(String(e)),
		)
		return () => {
			live = false
		}
	}, [settings.extensions, settings.persistentProfile])

	const run = (call: () => Promise<ExtensionsState | null>): void => {
		setError(null)
		call().then(
			(next) => next && setState(next),
			(e: unknown) => setError(e instanceof Error ? e.message : String(e)),
		)
	}

	if (!state) return error ? <SettingRowShell title="Extensions" detail={error} warning /> : null
	return (
		<>
			<SettingRowShell title="Load unpacked" detail={error ?? loadUnpackedDetail(settings, state.running)} warning={error !== null}>
				<PillButton variant="outlined" size="small" disabled={!canLoad(settings)} onClick={() => run(() => zing.loadUnpackedExtension())}>
					Choose Folder
				</PillButton>
			</SettingRowShell>
			{state.items.map((item) => (
				<ExtensionItem key={item.key} item={item} settings={settings} running={state.running} run={run} />
			))}
		</>
	)
}

function ExtensionItem({ item, settings, running, run }: { item: ExtensionRow; settings: Settings; running: boolean; run: (call: () => Promise<ExtensionsState | null>) => void }) {
	const detail = extensionDetail(item, settings, running)
	return (
		<SettingRowShell title={item.version ? `${item.name} ${item.version}` : item.name} detail={detail.text} warning={detail.warning}>
			<div className="extension-controls" data-extension={item.key}>
				<Toggle aria-label={`Turn on ${item.name}`} checked={item.enabled} onChange={(on) => run(() => zing.setExtensionEnabled(item.key, on))} />
				<PillButton variant="ghost" size="small" aria-label={`Remove ${item.name}`} onClick={() => run(() => zing.removeExtension(item.key))}>
					Remove
				</PillButton>
			</div>
		</SettingRowShell>
	)
}

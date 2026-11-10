import { useEffect, useState } from 'react'
import type { DefaultBrowserState } from '@shared/ipc'
import { zing } from '../api'
import { PillButton } from '../components'
import { SettingRowShell } from './SettingRowShell'

/** Only an installed Zing is registered with Windows, so only then can Default apps pick it. */
export function DefaultBrowserRowView({ state }: { state: DefaultBrowserState | null }) {
	if (!state) return <SettingRowShell title="Default browser" />
	if (state.isDefault)
		return (
			<SettingRowShell title="Default browser">
				<span className="row-value">Zing is your default browser</span>
			</SettingRowShell>
		)
	if (!state.registered)
		return (
			<SettingRowShell title="Default browser">
				<span className="row-value">Install Zing to set it as default</span>
			</SettingRowShell>
		)
	return (
		<SettingRowShell title="Default browser" detail="Opens Windows' Default apps page, where you choose Zing for links.">
			<PillButton variant="outlined" size="small" onClick={() => void zing.openDefaultAppsSettings()}>
				Make default
			</PillButton>
		</SettingRowShell>
	)
}

/** Asked again when the sheet regains focus: the person may be back from choosing in Windows' Settings. */
export function DefaultBrowserRow() {
	const [state, setState] = useState<DefaultBrowserState | null>(null)
	useEffect(() => {
		const refresh = (): void => void zing.getDefaultBrowserState().then(setState)
		refresh()
		window.addEventListener('focus', refresh)
		return () => window.removeEventListener('focus', refresh)
	}, [])
	return <DefaultBrowserRowView state={state} />
}

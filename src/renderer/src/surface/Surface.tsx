import { AuthView } from '../account/AuthView'
import { useAccount } from '../account/useAccount'
import { useShellState, useToolPanels } from '../panels/hooks'
import { PanelHost } from '../panels/PanelHost'
import { SettingsSheet } from '../settings/SettingsSheet'
import { useAccentTheme } from '../settings/useAccentTheme'
import { useSettings } from '../settings/useSettings'
import { Sidebar } from '../sidebar/Sidebar'

/**
 * Root of the surface view main stacks above the page: the account gate, the
 * revealed sidebar, floating tool panels and the settings sheet. It is sized
 * to whatever it shows and hidden when empty, so it never takes the page's clicks.
 */
export function Surface() {
	useAccentTheme()
	const shell = useShellState()
	const panels = useToolPanels()
	const account = useAccount()
	const settings = useSettings()
	if (!shell || !account) return null
	if (shell.gated) return <AuthView account={account} />
	return (
		<>
			{shell.sidebarRevealed ? <Sidebar variant="floating" /> : null}
			<PanelHost state={panels} origin={shell.origin} />
			{shell.settingsOpen && settings ? (
				<SettingsSheet handle={settings} initialCategory={shell.settingsCategory} version={shell.appVersion} />
			) : null}
		</>
	)
}

import type { ExtensionRow } from '@shared/ipc'
import type { Settings } from '@shared/settings'

/** Why extensions aren't running now, as the settings stand; null when they run. */
export function notRunningReason(settings: Settings, running: boolean): string | null {
	if (!settings.persistentProfile) return 'Extensions need Stay signed in to sites.'
	if (!settings.extensions) return 'Allow extensions is off.'
	if (!running) return 'Relaunch Zing to run extensions.'
	return null
}

export function loadUnpackedDetail(settings: Settings, running: boolean): string {
	const reason = notRunningReason(settings, running)
	if (reason && !(settings.persistentProfile && settings.extensions)) return reason
	const copy = 'Pick an extension’s folder (the one with manifest.json). Zing copies it, so the original can move.'
	return reason ? `${copy} ${reason}` : copy
}

/** An installed extension's line: what's wrong with it, or whether and how it runs. Never quiet about one that does nothing. */
export function extensionDetail(item: ExtensionRow, settings: Settings, running: boolean): { text: string; warning: boolean } {
	if (item.problem) return { text: item.problem, warning: true }
	const reason = notRunningReason(settings, running)
	if (reason) return { text: `Not running. ${reason}`, warning: true }
	if (!item.enabled) return { text: 'Off.', warning: false }
	return { text: item.hasPopup ? 'On. Its popup opens from the extensions button in the toolbar.' : 'On.', warning: false }
}

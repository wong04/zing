import { app, shell } from 'electron'

const WEB_PROTOCOLS = ['http', 'https'] as const

/**
 * Lists Zing as a program that can open web links. Windows 10 and 11 do not
 * let an app make itself the default, so this only registers it; the user
 * picks Zing in Settings. Skipped when running from source, where the
 * executable is Electron itself and registering it would claim links for it.
 */
export function registerAsBrowserCandidate(): void {
	if (!app.isPackaged) return
	for (const protocol of WEB_PROTOCOLS) app.setAsDefaultProtocolClient(protocol)
}

/** Opens Windows' Default apps page. It never changes the default itself. */
export async function openDefaultAppsSettings(): Promise<void> {
	await shell.openExternal('ms-settings:defaultapps')
}

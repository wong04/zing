import { join } from 'node:path'
import { app, dialog, Menu, nativeTheme, session } from 'electron'
import { chromeUserAgent } from '@shared/browser/userAgent'
import { SettingsStore, changedSettings } from '@shared/settings'
import { registerIpcHandlers } from './ipc-handlers'
import { buildAppMenu } from './menu'
import { captureScreenshotOnLoad } from './screenshot'
import { DebouncedJsonWriter, loadSettingsFile } from './settings-file'
import { TaskbarIcon } from './taskbar-icon'
import { ZingWindow } from './zing-window'

const windows = new Set<ZingWindow>()

function findWindow(predicate: (w: ZingWindow) => boolean): ZingWindow | undefined {
	return [...windows].find(predicate)
}

function openSettings(): SettingsStore {
	const path = join(app.getPath('userData'), 'settings.json')
	const settings = new SettingsStore(loadSettingsFile(path))
	const writer = new DebouncedJsonWriter(path)
	settings.subscribe((s) => writer.schedule(changedSettings(s)))
	app.on('will-quit', () => writer.flush())
	return settings
}

function followTheme(settings: SettingsStore): void {
	nativeTheme.themeSource = settings.get('theme')
	settings.subscribe((s, key) => {
		if (key === 'theme') nativeTheme.themeSource = s.theme
	})
}

function start(): void {
	const settings = openSettings()
	followTheme(settings)
	// In-memory (no `persist:` prefix): browsing data never touches disk. Created
	// once here and handed to each window, never imported as a global.
	const browsingSession = session.fromPartition('zing-private')
	if (!settings.get('defaultUserAgent')) browsingSession.setUserAgent(chromeUserAgent(browsingSession.getUserAgent()))

	const appMenu = buildAppMenu((id, base) => findWindow((w) => w.window === base)?.run(id))
	Menu.setApplicationMenu(appMenu)
	registerIpcHandlers((event) => findWindow((w) => w.isTrusted(event.sender)))

	const first = new ZingWindow({
		session: browsingSession,
		settings,
		appMenu: () => appMenu,
		onClosed: (w) => windows.delete(w),
	})
	windows.add(first)
	const taskbarIcon = new TaskbarIcon()
	taskbarIcon.attach(first.window)
	void taskbarIcon.refresh().catch(fail)
	captureScreenshotOnLoad(first)
}

function fail(error: unknown): void {
	dialog.showErrorBox('Zing could not start', error instanceof Error ? error.message : String(error))
	app.exit(1)
}

app.setAppUserModelId('app.zing.browser')
app.whenReady().then(start).catch(fail)
app.on('window-all-closed', () => app.quit())

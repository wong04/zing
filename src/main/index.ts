import { join } from 'node:path'
import { app, dialog, Menu, nativeTheme, net, safeStorage, session } from 'electron'
import { chromeUserAgent } from '@shared/browser/userAgent'
import { SettingsStore, changedSettings } from '@shared/settings'
import { AccountStore } from './account'
import { AdBlocker, strictFetch } from './adblock'
import { registerAsBrowserCandidate } from './default-browser'
import { registerIpcHandlers } from './ipc-handlers'
import { openLibrary } from './library-store'
import { installPageFeatures } from './page-actions'
import { LinkInbox } from './launch-args'
import { buildAppMenu } from './menu'
import { siteDataKeeperFor } from './privacy'
import { captureScreenshotOnLoad } from './screenshot'
import {
	SessionSaver,
	browsingPartition,
	discardSession,
	loadSession,
	queueRestore,
	sessionFilePath,
} from './session-restore'
import { DebouncedJsonWriter, loadSettingsFile } from './settings-file'
import { claimSingleInstance } from './single-instance'
import { registerStartPageScheme, StartPage } from './start-page'
import { TaskbarIcon } from './taskbar-icon'
import { recordMainErrorsForTests } from './test-hooks'
import { WindowManager, installQuitConfirmation } from './windows'

// Before app ready: the zing: scheme must be declared standard before any page loads it.
registerStartPageScheme()

const linkInbox = new LinkInbox()
let windows: WindowManager | undefined

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

function openAccount(): AccountStore {
	return new AccountStore({
		dir: join(app.getPath('userData'), 'account'),
		cipher: {
			isAvailable: () => safeStorage.isEncryptionAvailable(),
			encrypt: (plain) => safeStorage.encryptString(plain.toString('base64')),
			decrypt: (sealed) => Buffer.from(safeStorage.decryptString(sealed), 'base64'),
		},
		// Development and tests skip the gate; a packaged build never does.
		startAsGuest: !app.isPackaged && process.env['ZING_SKIP_ACCOUNT_GATE'] === '1',
	})
}

async function start(): Promise<void> {
	const settings = openSettings()
	followTheme(settings)
	// In-memory (no `persist:` prefix) unless the user opted in to a persistent profile:
	// browsing data never touches disk by default. Created once here and handed to
	// each window, never imported as a global.
	const browsingSession = session.fromPartition(browsingPartition(settings.get('persistentProfile')))
	if (!settings.get('defaultUserAgent')) browsingSession.setUserAgent(chromeUserAgent(browsingSession.getUserAgent()))

	const blocker = new AdBlocker({
		cachePath: join(app.getPath('userData'), 'adblock-engine.bin'),
		fetch: strictFetch((url) => net.fetch(url)),
		isEnabled: () => settings.get('blockTrackers'),
	})
	blocker.attach(browsingSession)
	// Awaited so the first page already loads with the engine in place; it deserializes in milliseconds.
	await blocker.loadCache().catch((error: unknown) => console.error(error))

	const sessionPath = sessionFilePath(app.getPath('userData'))
	if (settings.get('restoreTabs')) queueRestore(loadSession(sessionPath))
	else discardSession(sessionPath)
	const saver = new SessionSaver(
		sessionPath,
		{
			get activeId() {
				return windows?.primary?.tabs.activeId
			},
			get records() {
				return windows?.primary?.tabs.records ?? []
			},
		},
		settings,
	)
	app.on('will-quit', () => saver.flush())

	new StartPage(settings).serve(browsingSession)
	const library = openLibrary(join(app.getPath('userData'), 'library.json'))
	app.on('will-quit', () => library.flush())

	const manager = new WindowManager({
		onCreated: (w) =>
			w.onTabsChanged(() => {
				if (w.isPrimary) saver.changed()
			}),
		session: browsingSession,
		settings,
		appMenu: () => appMenu,
		siteData: siteDataKeeperFor(settings.get('persistentProfile'), browsingSession),
		account: openAccount(),
		library,
	})
	windows = manager
	const appMenu = buildAppMenu((id, base) => manager.find((w) => w.window === base)?.run(id))
	Menu.setApplicationMenu(appMenu)
	registerIpcHandlers((event) => manager.find((w) => w.isTrusted(event.sender)))
	installQuitConfirmation(manager, settings, fail)
	installPageFeatures(
		browsingSession,
		(contents) => manager.find((w) => w.features.owns(contents))?.features,
		() => manager.mostRecent()?.features,
	)
	registerAsBrowserCandidate()

	const first = manager.create()
	first.window.once('ready-to-show', () => {
		linkInbox.attach((url) => manager.openLink(url))
		const report = (error: unknown): void => console.error('Could not refresh the blocklists', error)
		blocker.refreshIfStale().catch(report)
		blocker.scheduleRefresh(report)
	})
	const taskbarIcon = new TaskbarIcon()
	taskbarIcon.attach(first.window)
	void taskbarIcon.refreshReporting()
	captureScreenshotOnLoad(first)
}

function fail(error: unknown): void {
	console.error(error)
	dialog.showErrorBox('Zing could not start', error instanceof Error ? error.message : String(error))
	app.exit(1)
}

recordMainErrorsForTests()
app.setAppUserModelId('app.zing.browser')
if (claimSingleInstance(linkInbox, () => windows?.mostRecent()?.reveal())) app.whenReady().then(start).catch(fail)
else app.quit()
app.on('window-all-closed', () => app.quit())

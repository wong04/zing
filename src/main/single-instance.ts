import { app } from 'electron'
import { LinkInbox, parseLaunchUrls } from './launch-args'

/**
 * Claims the single-instance lock. Returns false when another Zing already
 * runs (the caller should quit); that instance receives this one's links.
 * Links, including the one this process was launched with, go into the inbox,
 * which holds them until a window is ready to open them.
 */
export function claimSingleInstance(inbox: LinkInbox, onSecondLaunch: () => void): boolean {
	if (!app.requestSingleInstanceLock()) return false
	app.on('second-instance', (_event, argv) => {
		const urls = parseLaunchUrls(argv)
		for (const url of urls) inbox.push(url)
		if (urls.length === 0) onSecondLaunch()
	})
	for (const url of parseLaunchUrls(process.argv)) inbox.push(url)
	return true
}

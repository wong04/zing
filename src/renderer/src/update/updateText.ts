import type { UpdateState } from '@shared/ipc'

/** What About says beside Check now; nothing before the first check. */
export function checkStatusText(state: UpdateState): string | undefined {
	switch (state.status) {
		case 'idle':
			return undefined
		case 'checking':
			return 'Checking…'
		case 'upToDate':
			return "You're up to date"
		case 'downloading':
			return 'Downloading…'
		case 'ready':
			return 'Ready: relaunch to update'
		case 'failed':
			return "Couldn't check"
		case 'unavailable':
			return 'Updates work in installed builds'
	}
}

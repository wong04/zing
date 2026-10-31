import type { ZingApi } from '@shared/ipc'

declare global {
	interface Window {
		readonly zing: ZingApi
	}
}

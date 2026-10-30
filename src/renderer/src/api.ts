import type { ZingApi } from '@shared/ipc'

/** The preload's narrow bridge. The only way the renderer reaches main. */
export const zing: ZingApi = window.zing

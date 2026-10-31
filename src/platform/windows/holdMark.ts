import type { MarkOfTheWeb } from './security'

export interface MarkStore {
	read(): Promise<MarkOfTheWeb | undefined>
	write(mark: MarkOfTheWeb): Promise<void>
}

export interface HoldOptions {
	/** How long Chromium's own mark may take to appear. */
	readonly windowMs: number
	readonly pollMs: number
	readonly sleep: (ms: number) => Promise<void>
}

/**
 * Chromium stamps a generic `about:internet` Mark of the Web shortly after a
 * download finishes, and how shortly depends on how busy the PC is. Waiting a
 * fixed time and then writing ours lost the race under load (Chromium's mark
 * landed after ours and replaced the real HostUrl). Instead, ours is written
 * at once and re-written whenever something else replaces it, until Chromium's
 * write has been seen and overruled or the window runs out.
 */
export async function holdMarkOfTheWeb(store: MarkStore, ours: MarkOfTheWeb, options: HoldOptions): Promise<void> {
	await store.write(ours)
	for (let waited = 0; waited < options.windowMs; waited += options.pollMs) {
		await options.sleep(options.pollMs)
		const current = await store.read()
		if (current !== undefined && current.hostUrl === ours.hostUrl && current.zoneId === ours.zoneId) continue
		await store.write(ours)
		// Chromium writes its mark once; after overruling it there's nothing left to wait for.
		if (current) return
	}
}

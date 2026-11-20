// The probe's script is the host's (PageProbe.Source in host/Zing.Core/TabSleep.cs); this reads one frame's answer the same way.

export interface PageProbe {
	readonly scrollY: number
	readonly dirty: boolean
	readonly liveStream: boolean
	readonly pictureInPicture: boolean
}

export class PageProbeError extends Error {
	constructor(readonly value: unknown) {
		super(`Page probe returned an unexpected value: ${JSON.stringify(value)}`)
		this.name = 'PageProbeError'
	}
}

export function parsePageProbe(value: unknown): PageProbe {
	if (typeof value !== 'object' || value === null) throw new PageProbeError(value)
	const { scrollY, dirty, liveStream, pictureInPicture } = value as Record<string, unknown>
	if (typeof scrollY !== 'number' || !Number.isFinite(scrollY)) throw new PageProbeError(value)
	if (typeof dirty !== 'boolean' || typeof liveStream !== 'boolean' || typeof pictureInPicture !== 'boolean') throw new PageProbeError(value)
	return { scrollY: Math.max(0, scrollY), dirty, liveStream, pictureInPicture }
}

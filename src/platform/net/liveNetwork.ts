import { dohResolve } from './doh'

/**
 * Whether DoH works from here. Some networks block or intercept the resolvers,
 * which is exactly what the feature exists to detect, and no pinned fetch can
 * succeed there.
 */
export async function dohReachable(): Promise<boolean> {
	try {
		await dohResolve('example.com', AbortSignal.timeout(15_000))
		return true
	} catch (error) {
		console.warn(`DoH is not reachable from this network, skipping DoH-dependent tests: ${String(error)}`)
		return false
	}
}

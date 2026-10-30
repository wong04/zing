import net from 'node:net'
import { describe, expect, it } from 'vitest'
import { RelayStrategy } from '../../shared/unblock/relayEngine'
import { CancelledError } from './abort'
import { tcpProbe } from './probes'
import { strategyOrder } from './relayFetch'

describe('tcpProbe', () => {
	it('rejects with CancelledError the moment the signal fires', async () => {
		// 192.0.2.0/24 is reserved for documentation, so the SYN goes nowhere and the probe would hang.
		const controller = new AbortController()
		const pending = tcpProbe('192.0.2.1', controller.signal, 5_000)
		const started = Date.now()
		controller.abort()
		await expect(pending).rejects.toBeInstanceOf(CancelledError)
		expect(Date.now() - started).toBeLessThan(100)
	})

	it('rejects immediately for an already-aborted signal', async () => {
		await expect(tcpProbe('127.0.0.1', AbortSignal.abort(), 5_000)).rejects.toBeInstanceOf(CancelledError)
	})
})

describe('tcpProbe on loopback', () => {
	it('is false when nothing listens on 443', async () => {
		const listening = await new Promise<boolean>((resolve) => {
			const probe = net.connect({ host: '127.0.0.1', port: 443 })
			probe.once('connect', () => {
				probe.destroy()
				resolve(true)
			})
			probe.once('error', () => resolve(false))
		})
		// Skip the assertion on a machine that really has something on 443.
		if (listening) return
		expect(await tcpProbe('127.0.0.1', new AbortController().signal, 2_000)).toBe(false)
	})
})

describe('strategyOrder', () => {
	const all = new Set(Object.values(RelayStrategy))

	it('keeps Krackn order', () => {
		expect(strategyOrder(all)).toEqual(['Direct', 'DoH + IP pinning', 'Reader relay', 'Wayback archive'])
	})
	it('moves the preferred strategy first', () => {
		expect(strategyOrder(all, RelayStrategy.wayback)[0]).toBe('Wayback archive')
	})
	it('ignores a preferred strategy that is not enabled', () => {
		expect(strategyOrder(new Set([RelayStrategy.direct]), RelayStrategy.wayback)).toEqual(['Direct'])
	})
})

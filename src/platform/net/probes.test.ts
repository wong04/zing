import { describe, expect, it } from 'vitest'
import { RelayStrategy } from '../../shared/unblock/relayEngine'
import { strategyOrder } from './relayFetch'

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

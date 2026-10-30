// Port of section A of Krackn's Tests/unit/main.swift (89 assertions), plus
// tests for the helpers around it.

import { describe, expect, it } from 'vitest'
import {
	answersDiffer,
	blockDetail,
	blockTitle,
	BlockKind,
	classify,
	isSinkhole,
	recommendedStrategies,
	systemProbeTarget,
	type BlockObservations,
} from './blockDiagnostics'

const bools = [true, false] as const

function allCombinations(): BlockObservations[] {
	return bools.flatMap((systemTLSOK) =>
		bools.flatMap((sinkholed) =>
			bools.flatMap((systemAnswerEmpty) =>
				bools.flatMap((publicTCPOK) =>
					bools.map((publicTLSOK) => ({ systemTLSOK, sinkholed, systemAnswerEmpty, publicTCPOK, publicTLSOK })),
				),
			),
		),
	)
}

describe('BlockDiagnostics.classify — all 32 combinations', () => {
	it('keeps every invariant', () => {
		// Krackn's 89 checks, plus the table size it only prints.
		expect.assertions(90)
		const table = allCombinations().map((o) => ({ o, k: classify(o) }))
		expect(table).toHaveLength(32)

		for (const { o, k } of table) {
			const { systemTLSOK: sysTLS, sinkholed: sink, systemAnswerEmpty: empty, publicTCPOK: tcp, publicTLSOK: tls } = o
			const label = JSON.stringify(o)
			if (sysTLS) expect(k, `sysTLS implies notBlocked ${label}`).toBe(BlockKind.notBlocked)
			if (!sysTLS && sink && tcp) expect(k, `sinkhole+tcp -> dnsTampering ${label}`).toBe(BlockKind.dnsTampering)
			if (!sysTLS && !sink && !empty && !tcp && !tls) expect(k, `no reachability -> ipBlocked ${label}`).toBe(BlockKind.ipBlocked)
			if (!sysTLS && !sink && !empty && tcp && !tls) expect(k, `tcp no tls -> tlsInterception ${label}`).toBe(BlockKind.tlsInterception)
			if (!sysTLS && !sink && !empty && tls) expect(k, `public tls ok -> dnsTampering ${label}`).toBe(BlockKind.dnsTampering)
			expect(recommendedStrategies(k).length, `every verdict has advice ${label}`).toBeGreaterThan(0)
			expect(blockDetail(k) !== '' && blockTitle(k) !== '', `every verdict has detail ${label}`).toBe(true)
		}

		// Detection must not have collapsed to always-notBlocked.
		const blockedCount = table.filter(({ k }) => k !== BlockKind.notBlocked).length
		expect(blockedCount, `only ${blockedCount}/32 non-notBlocked`).toBeGreaterThanOrEqual(12)
	})
})

// Beyond Krackn's suite: the helpers diagnose() feeds into classify.
describe('isSinkhole', () => {
	it.each([
		['0.0.0.0', true],
		['127.0.0.1', true],
		['10.1.2.3', true],
		['192.168.0.1', true],
		['172.16.5.5', true],
		['93.184.216.34', false],
		['172.17.0.1', true],
		['172.31.255.255', true],
		['172.15.255.255', false],
		['172.32.0.1', false],
		['127.0.0.53', true],
		['0.1.2.3', true],
		['169.254.169.254', true],
		['169.253.0.1', false],
		['192.169.0.1', false],
		['11.0.0.1', false],
		['100.64.0.1', false],
		['::1', true],
		['2606:4700::1111', false],
		['not-an-ip', false],
		['10.example.com', false],
	])('%s -> %s', (ip, want) => {
		expect(isSinkhole(ip)).toBe(want)
	})
})

describe('answersDiffer', () => {
	it('flags disjoint answers (normal for anycast CDNs, so only a note)', () => {
		expect(answersDiffer(['157.240.1.35'], ['157.240.22.174'])).toBe(true)
	})
	it('any overlap means they agree', () => {
		expect(answersDiffer(['1.1.1.1', '2.2.2.2'], ['2.2.2.2'])).toBe(false)
	})
	it('no system answer is not a disagreement', () => {
		expect(answersDiffer([], ['1.1.1.1'])).toBe(false)
	})
})

describe('systemProbeTarget', () => {
	it('skips sinkholes', () => {
		expect(systemProbeTarget(['0.0.0.0', '93.184.216.34'])).toBe('93.184.216.34')
	})
	it('has nothing to probe when every answer is a sinkhole', () => {
		expect(systemProbeTarget(['0.0.0.0', '10.0.0.1'])).toBeUndefined()
	})
})

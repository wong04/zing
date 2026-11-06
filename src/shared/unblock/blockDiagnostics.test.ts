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
import vectors from './blockDiagnostics.vectors.json'

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

// The same 32 cases are read by the C# port's tests (Zing.Core.Tests/Net/BlockDiagnosticsTests.cs).
describe('BlockDiagnostics.classify — shared vectors', () => {
	it('covers every combination once', () => {
		expect(vectors.classify).toHaveLength(32)
		expect(new Set(vectors.classify.map((o) => JSON.stringify([o.systemTLSOK, o.sinkholed, o.systemAnswerEmpty, o.publicTCPOK, o.publicTLSOK]))).size).toBe(32)
	})
	it.each(vectors.classify)('sysTLS=$systemTLSOK sink=$sinkholed empty=$systemAnswerEmpty tcp=$publicTCPOK tls=$publicTLSOK -> $kind', ({ kind, ...observed }) => {
		expect(classify(observed)).toBe(kind)
	})
})

// Beyond Krackn's suite: the helpers diagnose() feeds into classify. Their cases are shared with the C# port too.
describe('isSinkhole', () => {
	it.each(vectors.isSinkhole as [string, boolean][])('%s -> %s', (ip, want) => {
		expect(isSinkhole(ip)).toBe(want)
	})
})

describe('answersDiffer', () => {
	it.each(vectors.answersDiffer)('$system vs $public -> $differ', ({ system, public: publicDNS, differ }) => {
		expect(answersDiffer(system, publicDNS)).toBe(differ)
	})
})

describe('systemProbeTarget', () => {
	it.each(vectors.systemProbeTarget)('$system -> $target', ({ system, target }) => {
		expect(systemProbeTarget(system)).toBe(target ?? undefined)
	})
})

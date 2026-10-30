// Krackn has no tests for these ThreatScanner pieces (sniffType's are in
// threatModel.test.ts with the rest of Tests/security); these pin the ported
// behaviour.

import { describe, expect, it } from 'vitest'
import { downloadNotice, ThreatLevel, verdictLevel } from './threatModel'
import {
	checkNavigation,
	inspectDownload,
	isRefusalAnswer,
	pathExtension,
	recordScan,
	sniffType,
	type ScanRecord,
} from './threatScanner'

const none: ReadonlySet<string> = new Set()

describe('sniffType', () => {
	it('needs at least four bytes', () => {
		expect(sniffType(Uint8Array.from([0x89, 0x50, 0x4E]))).toBeUndefined()
	})
	it.each([
		[[0x47, 0x49, 0x46, 0x38], 'gif'],
		[[0x1F, 0x8B, 0x08, 0x00], 'gz'],
		[[0x37, 0x7A, 0xBC, 0xAF], '7z'],
		[[0x52, 0x61, 0x72, 0x21], 'rar'],
		[[0xCA, 0xFE, 0xBA, 0xBE], 'macho'],
	])('%j -> %s', (bytes, want) => {
		expect(sniffType(Uint8Array.from(bytes))).toBe(want)
	})
})

describe('pathExtension', () => {
	it.each([
		['archive.tar.gz', 'gz'],
		['README', ''],
		['.bashrc', ''],
		['trailing.', ''],
	])('%s -> %s', (name, want) => {
		expect(pathExtension(name)).toBe(want)
	})
})

describe('inspectDownload', () => {
	it('a PNG that is really a Windows program is flagged on its header', () => {
		const verdict = inspectDownload('cat.png', Uint8Array.from([0x4D, 0x5A, 0x90, 0x00]))
		expect(verdict.findings.map((f) => f.title)).toContain("Contents don't match the extension")
	})
	it('an honest PDF is clean', () => {
		const verdict = inspectDownload('report.pdf', Uint8Array.from([0x25, 0x50, 0x44, 0x46]))
		expect(verdictLevel(verdict)).toBe(ThreatLevel.clean)
		expect(downloadNotice(verdict, 'report.pdf')).toBe('Saved report.pdf to Downloads. Scanned — clean.')
	})
})

describe('isRefusalAnswer', () => {
	it.each([
		[[], true],
		[['0.0.0.0'], true],
		[['0.0.0.0', '::'], true],
		[['93.184.216.34'], false],
		[['0.0.0.0', '93.184.216.34'], false],
	])('%j -> %s', (addresses, want) => {
		expect(isRefusalAnswer(addresses)).toBe(want)
	})
})

describe('checkNavigation', () => {
	it('lets a clean site through', () => {
		expect(checkNavigation(new URL('https://github.com/'), none, none)).toBeUndefined()
	})
	it('stops an impersonation', () => {
		expect(checkNavigation(new URL('https://apple.com.secure-login.tk/'), none, none)).toBeDefined()
	})
	it('ignores non-web schemes', () => {
		expect(checkNavigation(new URL('file:///C:/apple.com.secure-login.tk'), none, none)).toBeUndefined()
	})
	it('respects a host the user allowed', () => {
		const allowed = new Set(['apple.com.secure-login.tk'])
		expect(checkNavigation(new URL('https://apple.com.secure-login.tk/'), allowed, none)).toBeUndefined()
	})
	it('blocks a host the filtering resolvers refused', () => {
		const verdict = checkNavigation(new URL('https://example.com/'), none, new Set(['example.com']))
		expect(verdict && verdictLevel(verdict)).toBe(ThreatLevel.dangerous)
	})
})

describe('recordScan', () => {
	const entry = (subject: string): ScanRecord => ({
		when: new Date(0),
		subject,
		kind: 'Site',
		verdict: { subject, findings: [] },
	})
	it('puts the newest first and keeps 50', () => {
		let history: ScanRecord[] = []
		for (let i = 0; i < 60; i++) history = recordScan(history, entry(String(i)))
		expect(history).toHaveLength(50)
		expect(history[0]?.subject).toBe('59')
	})
})

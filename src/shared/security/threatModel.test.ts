// Port of Krackn's Tests/security/main.swift (69 assertions), plus the Windows
// executable checks Zing adds on top.

import { describe, expect, it } from 'vitest'
import { isRawIPv4 } from '../browser/registrableDomain'
import {
	inspectContent,
	inspectFilename,
	inspectHost,
	inspectURL,
	isBlocking,
	ThreatLevel,
	verdictLevel,
	type ThreatFinding,
	type ThreatVerdict,
} from './threatModel'
import { sniffType } from './threatScanner'

function verdictFor(raw: string): ThreatVerdict {
	return inspectURL(new URL(raw))
}

function fileLevel(name: string): ThreatLevel {
	return verdictLevel({ subject: name, findings: inspectFilename(name) })
}

function hasFinding(host: string, text: string): boolean {
	return inspectHost(host).some((finding) => finding.title.includes(text))
}

describe('real sites must not be flagged', () => {
	// Every one of these is an ordinary site. A false positive here is worse than
	// a missed detection, because it teaches the user to ignore the warning.
	it.each([
		'https://www.google.com/search?q=hello',
		'https://github.com/apple/swift',
		'https://en.wikipedia.org/wiki/Main_Page',
		'https://news.bbc.co.uk/news',
		'https://docs.google.com/document/d/abc123/edit',
		'https://mail.google.com/mail/u/0',
		'https://www.apple.com/uk/mac/',
		'https://appleinsider.com/articles/latest',
		'https://stackoverflow.com/questions/12345',
		'https://accounts.google.com/signin',
		'https://login.microsoftonline.com/common/oauth2',
		'https://secure.paypal.com/myaccount',
		'https://www.amazon.co.uk/gp/cart',
		'https://open.spotify.com/playlist/xyz',
		'https://developer.mozilla.org/en-US/docs/Web',
		'https://arxiv.org/abs/2401.00001',
	])('allows %s', (site) => {
		expect(verdictLevel(verdictFor(site))).toBeLessThanOrEqual(ThreatLevel.notice)
	})
})

describe('impersonation must be blocked', () => {
	// The brand appears in the host but isn't the registrable domain.
	it.each([
		'https://apple.com.secure-login.tk/verify',
		'https://paypal.account-update.xyz/signin',
		'https://signin-netflix.billing-verify.top/',
		'https://icloud-find-my.support-recovery.ga/',
		'https://microsoft-office365.login-verify.cf/',
		// Credentials in the URL disguise the real destination.
		'https://www.google.com@evil.example.net/',
	])('blocks %s', (raw) => {
		expect(isBlocking(verdictFor(raw))).toBe(true)
	})
})

describe('suspicious but not blocked', () => {
	it.each([
		// Punycode is legitimate for real internationalised domains, so it warns.
		'https://xn--80ak6aa92e.com/',
		// Raw IP over plain HTTP.
		'http://185.243.115.24/payload',
		// High-risk TLD that collides with a filename.
		'https://invoice-march.zip/',
		// Deep subdomain nesting pushes the real domain off the address bar.
		'https://a.b.c.d.e.f.example.org/',
	])('warns %s', (raw) => {
		expect(verdictLevel(verdictFor(raw))).toBe(ThreatLevel.suspicious)
	})
})

describe('host inspection details', () => {
	it('names the impersonated brand', () => {
		expect(hasFinding('apple.com.evil.tk', 'Impersonates')).toBe(true)
	})
	it('the real apple.com is not flagged as impersonating itself', () => {
		expect(hasFinding('apple.com', 'Impersonates')).toBe(false)
	})
	it("apple's own subdomain is not flagged", () => {
		expect(hasFinding('support.apple.com', 'Impersonates')).toBe(false)
	})
})

describe('IPv4 detection', () => {
	it.each([
		['192.168.1.1', true],
		['8.8.8.8', true],
		['255.255.255.255', true],
		['256.1.1.1', false], // out of range
		['1.2.3', false], // too few parts
		['example.com', false],
		['1.2.3.4.5', false],
	])('%s isIP=%s', (host, want) => {
		expect(isRawIPv4(host)).toBe(want)
	})
})

describe('filenames', () => {
	it.each([
		// Ordinary documents.
		['report.pdf', ThreatLevel.clean],
		['holiday.jpg', ThreatLevel.clean],
		['archive.zip', ThreatLevel.clean],
		['notes.txt', ThreatLevel.clean],
		['presentation.pptx', ThreatLevel.clean],
		// Programs are worth a warning but aren't malicious by themselves.
		['Installer.dmg', ThreatLevel.suspicious],
		['setup.pkg', ThreatLevel.suspicious],
		['build.sh', ThreatLevel.suspicious],
		// Macro-bearing documents.
		['invoice.docm', ThreatLevel.suspicious],
		['budget.xlsm', ThreatLevel.suspicious],
		// A document extension followed by an executable one is a deliberate disguise.
		['invoice.pdf.app', ThreatLevel.dangerous],
		['photo.jpg.command', ThreatLevel.dangerous],
		['statement.docx.exe', ThreatLevel.dangerous],
		// Right-to-left override reverses how the name displays.
		['photo‮gpj.app', ThreatLevel.dangerous],
	])('%s -> level %s', (name, want) => {
		expect(fileLevel(name)).toBe(want)
	})
})

describe('content vs extension', () => {
	it.each([
		['png', 'png', false],
		['jpg', 'jpeg', false], // interchangeable, not a mismatch
		['jpeg', 'jpg', false],
		['tif', 'tiff', false],
		['pdf', 'macho', true], // a "PDF" that's actually a binary
		['png', 'exe', true],
		['txt', undefined, false], // unknown header proves nothing
	] as const)('.%s vs %s flagged=%s', (ext, sniffed, shouldFlag) => {
		expect(inspectContent(ext, sniffed).length > 0).toBe(shouldFlag)
	})
})

describe('magic numbers', () => {
	it.each([
		[[0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0], 'png'],
		[[0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0], 'jpg'],
		[[0x25, 0x50, 0x44, 0x46, 0, 0, 0, 0], 'pdf'],
		[[0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0], 'zip'],
		[[0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0], 'macho'],
		[[0x4D, 0x5A, 0x90, 0x00, 0, 0, 0, 0], 'exe'],
		[[0x23, 0x21, 0x2F, 0x62, 0, 0, 0, 0], 'sh'],
		[[0x00, 0x01, 0x02, 0x03, 0, 0, 0, 0], undefined],
	])('%j -> %s', (bytes, want) => {
		expect(sniffType(Uint8Array.from(bytes))).toBe(want)
	})
})

describe('verdict aggregation', () => {
	const finding = (level: ThreatLevel, title: string): ThreatFinding => ({ level, title, detail: '' })
	const mixedFindings: ThreatVerdict = {
		subject: 'x',
		findings: [
			finding(ThreatLevel.notice, 'a'),
			finding(ThreatLevel.dangerous, 'b'),
			finding(ThreatLevel.suspicious, 'c'),
		],
	}

	it('verdict takes the worst finding', () => {
		expect(verdictLevel(mixedFindings)).toBe(ThreatLevel.dangerous)
	})
	it('dangerous blocks', () => {
		expect(isBlocking(mixedFindings)).toBe(true)
	})
	it('empty verdict is clean', () => {
		const empty: ThreatVerdict = { subject: 'x', findings: [] }
		expect(verdictLevel(empty) === ThreatLevel.clean && !isBlocking(empty)).toBe(true)
	})
	it('a notice never blocks', () => {
		expect(isBlocking({ subject: 'x', findings: [finding(ThreatLevel.notice, 'signed')] })).toBe(false)
	})
})

// Beyond Krackn: the Windows types that run on a double-click. Each must warn
// on its own and be called a disguise behind a document extension.
describe('Windows executables (Zing addition)', () => {
	const windowsExecutables = ['exe', 'scr', 'msi', 'lnk', 'ps1', 'hta', 'vbs', 'bat', 'cmd', 'js', 'jse', 'wsf', 'cpl']

	it.each(windowsExecutables)('setup.%s is suspicious', (ext) => {
		expect(fileLevel(`setup.${ext}`)).toBe(ThreatLevel.suspicious)
	})
	it.each(windowsExecutables)('invoice.pdf.%s is dangerous', (ext) => {
		expect(fileLevel(`invoice.pdf.${ext}`)).toBe(ThreatLevel.dangerous)
	})
	it('names both halves of the disguise', () => {
		expect(inspectFilename('Invoice.PDF.lnk').map((f) => f.title)).toContain('Disguised as a .pdf but is a .lnk')
	})
	it('an RTL override hiding an .exe is dangerous', () => {
		expect(fileLevel('invoice‮fdp.exe')).toBe(ThreatLevel.dangerous)
	})
})

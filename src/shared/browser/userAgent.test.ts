// Ports the intent of Krackn `Tests/unit` "user agent — follows the installed Safari":
// the identifier follows the engine actually running, in the browser's exact format.
import { describe, expect, it } from 'vitest'
import { UserAgentFormatError, chromeUserAgent, reducedChromeUserAgent } from './userAgent'

const electronOnWindows =
	'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
	'Zing/0.0.1 Chrome/140.0.7339.133 Electron/38.2.0 Safari/537.36'

describe('user agent — follows the running Chromium', () => {
	it('Chrome\'s exact format', () => {
		expect(chromeUserAgent(electronOnWindows)).toBe(
			'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
				'Chrome/140.0.7339.133 Safari/537.36',
		)
	})
	it('drops the Electron token', () => {
		expect(chromeUserAgent(electronOnWindows)).not.toContain('Electron')
	})
	it('drops the app-name token, whatever its case', () => {
		const lower = electronOnWindows.replace('Zing/0.0.1', 'zing/0.0.1')
		expect(chromeUserAgent(electronOnWindows)).not.toMatch(/zing/i)
		expect(chromeUserAgent(lower)).not.toMatch(/zing/i)
	})
	it('the identifier carries the running version, not a constant', () => {
		const newer = electronOnWindows.replace('Chrome/140.0.7339.133', 'Chrome/141.0.7390.54')
		expect(chromeUserAgent(newer)).toContain('Chrome/141.0.7390.54 ')
		expect(chromeUserAgent(newer)).not.toContain('Chrome/140')
	})
	it('keeps the platform comment as given', () => {
		const mac = electronOnWindows.replace('(Windows NT 10.0; Win64; x64)', '(Macintosh; Intel Mac OS X 10_15_7)')
		expect(chromeUserAgent(mac)).toMatch(/^Mozilla\/5\.0 \(Macintosh; Intel Mac OS X 10_15_7\) /)
	})
	it('an already clean Chrome UA is unchanged', () => {
		const clean = chromeUserAgent(electronOnWindows)
		expect(chromeUserAgent(clean)).toBe(clean)
	})
	it('rejects junk', () => {
		expect(() => chromeUserAgent('')).toThrow(UserAgentFormatError)
		expect(() => chromeUserAgent('Mozilla/5.0 (Macintosh) Version/26.6.2 Safari/605.1.15')).toThrow(
			UserAgentFormatError,
		)
	})
})

describe('reduced user agent — what stock Chrome on Windows sends', () => {
	it('Chrome reduced format: major version only', () => {
		expect(reducedChromeUserAgent(electronOnWindows)).toBe(
			'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
				'Chrome/140.0.0.0 Safari/537.36',
		)
	})
	it('follows the running major version, not a constant', () => {
		const newer = electronOnWindows.replace('Chrome/140.0.7339.133', 'Chrome/141.0.7390.54')
		expect(reducedChromeUserAgent(newer)).toContain(' Chrome/141.0.0.0 ')
	})
	it('the platform token is frozen, whatever the machine reports', () => {
		const arm = electronOnWindows.replace('(Windows NT 10.0; Win64; x64)', '(Windows NT 10.0; ARM64)')
		expect(reducedChromeUserAgent(arm)).toContain('(Windows NT 10.0; Win64; x64)')
	})
	it('no Electron or app-name token', () => {
		expect(reducedChromeUserAgent(electronOnWindows)).not.toMatch(/Electron|zing/i)
	})
	it('a version that only looks like Chrome is not taken', () => {
		expect(() => reducedChromeUserAgent('Mozilla/5.0 HeadlessChrome/140.0.1 Safari/537.36')).toThrow(UserAgentFormatError)
	})
	it('rejects junk', () => {
		expect(() => reducedChromeUserAgent('')).toThrow(UserAgentFormatError)
		expect(() => reducedChromeUserAgent('Mozilla/5.0 (Macintosh) Version/26.6.2 Safari/605.1.15')).toThrow(UserAgentFormatError)
	})
})

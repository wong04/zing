import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { formatNotices, overrideFile, type NoticePackage } from './notices'

const pkg = (name: string, version: string, over: Partial<NoticePackage> = {}): NoticePackage => ({
	name,
	version,
	license: 'MIT',
	licenseText: `${name} licence text`,
	source: 'npm',
	...over,
})

describe('formatNotices', () => {
	it('names the licence of Zing itself in the header', () => {
		expect(formatNotices([]).split('\n').slice(0, 5).join('\n')).toContain('MIT')
	})

	it('sorts and dedupes', () => {
		const text = formatNotices([pkg('zeta', '1.0.0'), pkg('alpha', '2.0.0'), pkg('alpha', '1.0.0'), pkg('zeta', '1.0.0', { source: 'nuget' })])
		const order = ['alpha 1.0.0', 'alpha 2.0.0', 'zeta 1.0.0'].map((entry) => text.indexOf(`\n${entry}\n`))
		expect(order.every((at) => at >= 0)).toBe(true)
		expect(order).toEqual([...order].sort((a, b) => a - b))
		expect(text.split('\nzeta 1.0.0\n')).toHaveLength(2)
	})

	it('ends with the WebView2 and Windows App SDK sections', () => {
		const text = formatNotices([pkg('alpha', '1.0.0')])
		const webView = text.indexOf('\nMicrosoft Edge WebView2 Runtime\n')
		const appSdk = text.indexOf('\nWindows App SDK\n')
		expect(text.indexOf('\nalpha 1.0.0\n')).toBeLessThan(webView)
		expect(webView).toBeLessThan(appSdk)
		expect(text.slice(appSdk)).toContain('https://')
		expect(text.slice(webView, appSdk)).toContain('https://')
	})

	it('credits the bundled Public Suffix List under the MPL 2.0', () => {
		const text = formatNotices([])
		const section = text.slice(text.indexOf('\nPublic Suffix List\n'))
		expect(section).toContain('Mozilla Public License 2.0')
		expect(section).toContain('https://publicsuffix.org/')
		expect(section).toContain('https://mozilla.org/MPL/2.0/')
	})

	it('credits the bundled Inter font under the SIL OFL 1.1', () => {
		const text = formatNotices([])
		const section = text.slice(text.indexOf('\nInter\n'))
		expect(section).toContain('SIL Open Font License 1.1')
		expect(section).toContain('https://github.com/rsms/inter')
		expect(section).toContain('Copyright (c) 2016 The Inter Project Authors')
		expect(section).toContain('PERMISSION AND CONDITIONS')
	})

	it('labels the WebView2 link as more information, not licence terms', () => {
		expect(formatNotices([])).toContain('More information: https://developer.microsoft.com/microsoft-edge/webview2/')
	})

	it('throws naming a package without a licence', () => {
		expect(() => formatNotices([pkg('alpha', '1.0.0'), pkg('no-licence', '3.1.4', { licenseText: '  \n' })])).toThrow(/no-licence@3\.1\.4/)
	})
})

describe('licence text for packages that ship none', () => {
	const packages = ['onnxruntime-web', 'onnxruntime-common', 'guid-typescript', '@napi-rs/canvas-win32-x64-msvc', 'qrcode-generator']

	it.each(packages)('%s has a committed, non-empty licence text with a copyright line', (name) => {
		expect(existsSync(overrideFile(name))).toBe(true)
		const text = readFileSync(overrideFile(name), 'utf8')
		expect(text.trim()).not.toBe('')
		expect(text).toMatch(/Copyright/)
	})

	it.each(packages)('%s ships no licence file in node_modules, so its override is used', (name) => {
		const shipped = readdirSync(join(import.meta.dirname, '..', 'node_modules', name)).filter((file) => /^licen[cs]e([-.].*)?$/i.test(file))
		expect(shipped).toEqual([])
	})
})

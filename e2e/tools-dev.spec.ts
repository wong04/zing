import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

/** Opens the Toolbox, picks a tile, and returns the floating panel the tool lives in. */
async function openTool(zing: Launched, title: string): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').filter({ hasText: title }).click()
	const panel = surface.getByRole('dialog', { name: title })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

const result = (panel: Locator, label: string): Locator => panel.getByRole('group', { name: label, exact: true })
const base64url = (text: string): string => Buffer.from(text).toString('base64url')

test('JSON formats {"a":1} and a bad document shows the line of the error', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'JSON')
	await panel.getByRole('textbox', { name: 'JSON input' }).fill('{"a":1}')
	await expect(panel.getByRole('textbox', { name: 'JSON output' })).toHaveValue('{\n  "a": 1\n}')
	await panel.getByRole('textbox', { name: 'JSON input' }).fill('{\n\t"a": 1,\n\t"b": }')
	await expect(panel.getByRole('alert')).toContainText('Line 3')
	await zing.close()
})

test('valid JSON nested 20,000 deep shows a message instead of crashing the tool', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'JSON')
	await panel.getByRole('textbox', { name: 'JSON input' }).fill('['.repeat(20_000) + ']'.repeat(20_000))
	await expect(panel.getByRole('alert')).toContainText('Nested too deeply')
	await panel.getByRole('textbox', { name: 'JSON input' }).fill('{"a":1}')
	await expect(panel.getByRole('textbox', { name: 'JSON output' })).toHaveValue('{\n  "a": 1\n}')
	await zing.close()
})

test('Base64 round-trips URL-safe input without padding', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Base64')
	await panel.getByRole('radio', { name: 'Decode' }).click()
	await panel.getByRole('textbox', { name: 'Base64 input' }).fill('Pz8-Pg')
	await expect(panel.getByRole('textbox', { name: 'Base64 output' })).toHaveValue('??>>')
	await panel.getByRole('radio', { name: 'Encode' }).click()
	await panel.getByRole('textbox', { name: 'Base64 input' }).fill('??>>')
	await expect(panel.getByRole('textbox', { name: 'Base64 output' })).toHaveValue('Pz8+Pg==')
	await zing.close()
})

test('SHA-256 of abc, and of a file', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Hashes')
	await panel.getByRole('textbox', { name: 'Text to hash' }).fill('abc')
	await expect(result(panel, 'Digest')).toContainText('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
	await panel.getByRole('radio', { name: 'MD5' }).click()
	await expect(result(panel, 'Digest')).toContainText('900150983cd24fb0d6963f7d28e17f72')
	await panel.getByRole('radio', { name: 'SHA-256' }).click()

	const dir = mkdtempSync(join(tmpdir(), 'zing-e2e-hash-'))
	try {
		const file = join(dir, 'abc.txt')
		writeFileSync(file, 'abc')
		await panel.getByRole('textbox', { name: 'File path' }).fill(file)
		await panel.getByRole('button', { name: 'Hash file' }).click()
		await expect(result(panel, 'File digest')).toContainText('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
		await panel.getByRole('textbox', { name: 'File path' }).fill(join(dir, 'missing.bin'))
		await panel.getByRole('button', { name: 'Hash file' }).click()
		await expect(panel.getByRole('alert')).toContainText(/find|exist|not found/i)
	} finally {
		rmSync(dir, { recursive: true, force: true })
	}
	await zing.close()
})

test('a Copy button is off with nothing to copy, shows a refusal, and forgets its tick when the value changes', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Hashes')
	const surface = panel.page()
	const copy = result(panel, 'Digest').getByRole('button')
	await expect(copy).toBeDisabled()
	await panel.getByRole('textbox', { name: 'Text to hash' }).fill('abc')
	await expect(copy).toBeEnabled()
	// The page's clipboard is replaced, so nothing reaches the system clipboard.
	await surface.evaluate(() => {
		Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: () => Promise.resolve() } })
	})
	await copy.click()
	await expect(copy).toHaveAccessibleName('Copied')
	await panel.getByRole('textbox', { name: 'Text to hash' }).fill('abcd')
	await expect(copy).toHaveAccessibleName('Copy Digest')
	await copy.click()
	await expect(copy).toHaveAccessibleName('Copied')
	await expect(copy).toHaveAccessibleName('Copy Digest', { timeout: 5000 })
	await surface.evaluate(() => {
		Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: () => Promise.reject(new Error('refused')) } })
	})
	await copy.click()
	await expect(copy).toHaveAccessibleName('Copy failed')
	await zing.close()
})

test('HMAC-SHA-256 of the quick brown fox', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'HMAC')
	await panel.getByRole('textbox', { name: 'Secret key' }).fill('key')
	await panel.getByRole('textbox', { name: 'Message' }).fill('The quick brown fox jumps over the lazy dog')
	await expect(result(panel, 'HMAC-SHA-256')).toContainText('f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8')
	await zing.close()
})

test('a JWT decodes its payload', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'JWT Decoder')
	const token = [base64url('{"alg":"HS256","typ":"JWT"}'), base64url('{"name":"Ada","sub":"123"}'), 'c2ln'].join('.')
	await panel.getByRole('textbox', { name: 'JWT' }).fill(token)
	await expect(result(panel, 'Payload')).toContainText('"name": "Ada"')
	await expect(result(panel, 'Header')).toContainText('"alg": "HS256"')
	await expect(panel.getByText('Not verified: this only decodes the token')).toBeVisible()
	await expect(result(panel, 'Signature (not checked)')).toContainText('c2ln')
	await zing.close()
})

test('a cron line shows its next run', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Cron Parser')
	await panel.getByRole('textbox', { name: 'Cron expression' }).fill('*/15 * * * *')
	await expect(panel.getByText('every 15 minutes')).toBeVisible()
	await expect(result(panel, 'Next run 1')).toContainText(/\d/)
	await expect(result(panel, 'Next run 3')).toContainText(/\d/)
	await panel.getByRole('textbox', { name: 'Cron expression' }).fill('0 0 31 2 *')
	await expect(panel.getByRole('alert')).toBeVisible()
	await zing.close()
})

test('a catastrophic regex gives up after 2 seconds and the tool still responds', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Regex Tester')
	await panel.getByRole('textbox', { name: 'Test string' }).fill('a'.repeat(40) + '!')
	await panel.getByRole('textbox', { name: 'Pattern' }).fill('(a+)+$')
	await expect(panel.getByRole('alert')).toContainText('gave up after 2 seconds', { timeout: 10_000 })
	await panel.getByRole('textbox', { name: 'Test string' }).fill('aaa')
	await panel.getByRole('textbox', { name: 'Pattern' }).fill('a')
	await expect(panel.getByText('3 matches')).toBeVisible()
	await zing.close()
})

test('a 5 MB JSON paste keeps the tool and the chrome responsive', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'JSON')
	// One object of 330,000 keys in reverse order: sorting them is the expensive part (~0.75 s of work), while the
	// formatted text stays about the same size as the paste, so the cost measured is formatting and not display.
	const keys = 330_000
	const big = '{' + Array.from({ length: keys }, (_, i) => `"k${String(keys - i).padStart(6, '0')}":${i}`).join(',') + '}'
	expect(big.length).toBeGreaterThan(5 * 1024 * 1024)

	// A timer in the tool's own page, ticking every 20 ms. While the tool says it is formatting, every tick that
	// arrives is proof the page was free to run; the longest gap between those ticks is how long it was ever stuck.
	// Formatting on the main thread would give no such ticks at all: the page is blocked until the result is ready.
	const surface = panel.page()
	await expect(panel.getByRole('textbox', { name: 'JSON output' })).toBeVisible()
	await surface.evaluate(() => {
		const output = document.querySelector<HTMLTextAreaElement>('textarea[readonly]')
		if (!output) throw new Error('no output box')
		const beat = { formattingTicks: 0, longestGap: 0 }
		let previous: number | null = null
		Object.assign(globalThis, { beat })
		const timer = setInterval(() => {
			const now = performance.now()
			if (output.value !== '' || (document.body.textContent ?? '').includes('too large to show')) {
				clearInterval(timer)
				return
			}
			const formatting = (document.body.textContent ?? '').includes('Formatting a large document')
			if (formatting && previous !== null) beat.longestGap = Math.max(beat.longestGap, now - previous)
			if (formatting) beat.formattingTicks++
			previous = formatting ? now : null
		}, 20)
	})

	const pasted = panel.getByRole('textbox', { name: 'JSON input' }).fill(big)
	let slowestChrome = 0
	for (let i = 0; i < 8; i++) {
		const started = Date.now()
		await zing.chrome.evaluate(() => window.zing.getTabs())
		slowestChrome = Math.max(slowestChrome, Date.now() - started)
	}
	await pasted
	// 330,000 lines is past what the tool puts in a text box, so the result is offered for copying instead.
	await expect(panel.getByText(/too large to show/)).toBeVisible({ timeout: 30_000 })

	const beat = await surface.evaluate(() => (globalThis as unknown as { beat: { formattingTicks: number; longestGap: number } }).beat)
	expect(slowestChrome).toBeLessThan(1000)
	expect(beat.formattingTicks).toBeGreaterThan(10)
	expect(beat.longestGap).toBeLessThan(500)
	await zing.close()
})

test('a 5 MB array that expands to millions of lines is not put in the textarea, and the page stays responsive', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'JSON')
	const items = 2_500_000
	const big = '[' + '1,'.repeat(items - 1) + '1]'
	expect(big.length).toBeGreaterThanOrEqual(5_000_000)

	// The same timer-tick method as above, but it keeps running after the result arrives, so a long block while the
	// result is put on the page shows up as a long gap between two ticks.
	const surface = panel.page()
	await expect(panel.getByRole('textbox', { name: 'JSON output' })).toBeVisible()
	await surface.evaluate(() => {
		const beat = { ticks: 0, longestGap: 0, stop: false }
		let previous: number | null = null
		Object.assign(globalThis, { beat })
		const timer = setInterval(() => {
			const now = performance.now()
			// Pasting 5 MB into the input is slow in itself and is not what is measured: counting starts once the tool is formatting.
			if (previous === null && (document.body.textContent ?? '').includes('Formatting a large document')) previous = now
			if (previous !== null) {
				beat.longestGap = Math.max(beat.longestGap, now - previous)
				beat.ticks++
				previous = now
			}
			if (beat.stop) clearInterval(timer)
		}, 20)
	})
	await panel.getByRole('textbox', { name: 'JSON input' }).fill(big)
	await expect(panel.getByText(/too large to show/)).toBeVisible({ timeout: 60_000 })
	await expect(panel.getByText(/12\.5 MB/)).toBeVisible()
	await expect(panel.getByRole('textbox', { name: 'JSON output' })).toHaveCount(0)
	await expect(panel.getByRole('button', { name: 'Copy JSON output' })).toBeEnabled()
	// Let any late work on the page land before the timer is read.
	await surface.waitForTimeout(1500)
	const beat = await surface.evaluate(() => {
		const b = (globalThis as unknown as { beat: { ticks: number; longestGap: number; stop: boolean } }).beat
		b.stop = true
		return { ticks: b.ticks, longestGap: b.longestGap }
	})
	expect(beat.ticks).toBeGreaterThan(10)
	// Showing this result in a textarea blocked the page for 27 s when measured; the page's own cost of holding a 5 MB
	// input (about 0.5 s of layout here) is in this number too, so the bound is generous and still far below that.
	expect(beat.longestGap).toBeLessThan(1500)
	await zing.close()
})

test('Timestamps reads a Unix time', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Timestamps')
	await panel.getByRole('textbox', { name: 'Timestamp' }).fill('1753800000')
	await expect(result(panel, 'ISO 8601 (UTC)')).toContainText('2025-07-29T14:40:00Z')
	await zing.close()
})

test('URL Encoding escapes reserved characters', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'URL Encoding')
	await panel.getByRole('textbox', { name: 'URL input' }).fill('a b&c=d/é')
	await expect(panel.getByRole('textbox', { name: 'URL output' })).toHaveValue('a%20b%26c%3Dd%2F%C3%A9')
	await zing.close()
})

test('UUID generates five and can lowercase them', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'UUID')
	await panel.getByRole('button', { name: 'Generate 5' }).click()
	await expect(panel.getByTestId('uuid-row')).toHaveCount(5)
	await expect(panel.getByTestId('uuid-row').first()).toHaveText(/^[0-9A-F]{8}-[0-9A-F]{4}-4[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$/)
	await panel.getByRole('switch', { name: 'Lowercase' }).click()
	await expect(panel.getByTestId('uuid-row').first()).toHaveText(/^[0-9a-f-]{36}$/)
	await zing.close()
})

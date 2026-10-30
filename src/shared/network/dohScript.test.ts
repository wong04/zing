// Mirrors Krackn's "D. DoHProfile" checks in Tests/unit, for the PowerShell
// script that replaces the .mobileconfig. The scripts are parsed by the real
// PowerShell parser, the equivalent of Krackn parsing its plist.
import { spawnSync } from 'node:child_process'
import { describe, expect, it, vi } from 'vitest'
import { displayTitle, makeDisableScript, makeEnableScript, psQuote, RESOLVERS, scriptFileName, wireGuardCommands } from './dohScript'

// Each case starts Windows PowerShell, which can take seconds to cold-start on a busy CI runner.
vi.setConfig({ testTimeout: 30_000 })

// Never executes anything: only the parser runs, and it reports syntax errors.
function parseErrors(script: string): string[] {
	const encoded = Buffer.from(script, 'utf8').toString('base64')
	const command = [
		`$text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${encoded}'))`,
		'$errors = $null',
		'[void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$errors)',
		'$errors | ForEach-Object { $_.Message }',
	].join('; ')
	const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', command], { encoding: 'utf8' })
	if (result.error) throw result.error
	if (result.status !== 0) throw new Error(`PowerShell exited ${result.status}: ${result.stderr}`)
	return result.stdout.split(/\r?\n/).filter(Boolean)
}

const NAMES = ['AlgoVerse AI SEAL Test 1', 'A&B <x> "q"', "it's ‘quoted’", '', '   ', 'n'.repeat(300), 'café 🎉', 'line\nbreak']

describe('enable script', () => {
	for (const resolver of RESOLVERS) {
		const script = makeEnableScript(resolver)
		it(`${resolver.name} parses`, () => expect(parseErrors(script)).toEqual([]))
		it(`${resolver.name} has its url and addresses`, () => {
			expect(script).toContain(resolver.url)
			for (const address of resolver.addresses) expect(script).toContain(`'${address}'`)
		})
		it(`${resolver.name} requires admin and refuses Windows 10`, () => {
			expect(script.startsWith('#Requires -RunAsAdministrator')).toBe(true)
			expect(script).toContain('-lt 22000')
		})
		it(`${resolver.name} never falls back to plain DNS`, () => expect(script).toContain('-AllowFallbackToUdp $false'))
	}
	for (const name of NAMES) {
		it(`name '${name.slice(0, 18)}' parses`, () => expect(parseErrors(makeEnableScript(RESOLVERS[0]!, name))).toEqual([]))
		it(`name '${name.slice(0, 18)}' is preserved as the title`, () => {
			const expected = name.trim() ? name.trim() : 'Encrypted DNS — Cloudflare'
			expect(displayTitle(RESOLVERS[0]!, name)).toBe(expected)
		})
		it(`filename safe '${name.slice(0, 12)}'`, () => {
			const file = scriptFileName(RESOLVERS[0]!, name)
			expect(file).toMatch(/\.ps1$/)
			expect(file).not.toMatch(/[<>:"/\\|?*\n]/)
		})
	}
})

describe('the parser check itself', () => {
	it('does report broken PowerShell', () => expect(parseErrors("if ( 'x'").length).toBeGreaterThan(0))
})

describe('disable script', () => {
	it('parses and resets to automatic DNS', () => {
		const script = makeDisableScript()
		expect(parseErrors(script)).toEqual([])
		expect(script).toContain('-ResetServerAddresses')
	})
})

describe('quoting', () => {
	it('doubles single quotes, including typographic ones', () => expect(psQuote("it's ‘x’")).toBe("'it''s ''x'''"))
	it('a hostile name cannot break out of the string', () => {
		const script = makeEnableScript({ ...RESOLVERS[0]!, name: "x'; Remove-Item C:\\ -Recurse; '" })
		expect(parseErrors(script)).toEqual([])
		expect(script).toContain("'Done. DNS now goes to x''; Remove-Item C:\\ -Recurse; '' over HTTPS.'")
	})
})

describe('WireGuard for Windows', () => {
	const commands = wireGuardCommands('C:\\Users\\me\\VPN\\home.conf')
	it('installs and removes the tunnel service by name', () => {
		expect(commands.up).toContain("/installtunnelservice 'C:\\Users\\me\\VPN\\home.conf'")
		expect(commands.down).toContain("/uninstalltunnelservice 'home'")
	})
	it('commands parse as PowerShell', () => expect(parseErrors([commands.up, commands.down].join('\r\n'))).toEqual([]))
})

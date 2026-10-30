// Pure parsing tests, plus integration tests against this real Windows
// install. The false-positive cases come first, as in Krackn: a signed
// Windows binary must come back Valid, and this PC's protections must read
// as what they are.
import { mkdtemp, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { encodeCommand, parsePsDate } from './powershell'
import {
	bitLockerFromShell,
	checkSignature,
	defenderStatus,
	formatZoneIdentifier,
	INTERNET_ZONE,
	parsePosture,
	parseZoneIdentifier,
	readMarkOfTheWeb,
	scanFile,
	smartScreenFromRegistry,
	systemPosture,
	writeMarkOfTheWeb,
} from './security'

describe('Mark of the Web parsing', () => {
	it('reads what Chromium and Edge write', () => {
		expect(parseZoneIdentifier('[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://a.example/\r\nHostUrl=https://cdn.a.example/f.exe\r\n')).toEqual({
			zoneId: 3,
			referrerUrl: 'https://a.example/',
			hostUrl: 'https://cdn.a.example/f.exe',
		})
	})
	it('a stream without a ZoneId is not a mark', () => expect(parseZoneIdentifier('[ZoneTransfer]\r\n')).toBeUndefined())
	it('round-trips', () => {
		const mark = { zoneId: 3, hostUrl: 'https://x.example/a.zip' }
		expect(parseZoneIdentifier(formatZoneIdentifier(mark))).toEqual({ ...mark, referrerUrl: undefined })
	})
})

describe('registry and shell value mapping', () => {
	it('BitLocker shell codes', () => {
		expect([1, 2, 3, 5, 0, null].map(bitLockerFromShell)).toEqual(['on', 'off', 'on', 'suspended', 'unknown', 'unknown'])
	})
	it('SmartScreen policy wins over the Explorer value', () => {
		expect(smartScreenFromRegistry(0, 'Warn')).toBe('off')
		expect(smartScreenFromRegistry(null, 'Off')).toBe('off')
		expect(smartScreenFromRegistry(null, 'RequireAdmin')).toBe('on')
		expect(smartScreenFromRegistry(null, null)).toBe('unknown')
	})
	it('a single firewall profile comes back as an object, not an array', () => {
		expect(parsePosture({ SecureBoot: 1, BitLocker: 1, Firewall: { Name: 'Public', Enabled: 1 }, SmartScreenPolicy: null, SmartScreenExplorer: null }).firewall).toEqual([{ profile: 'Public', enabled: true }])
	})
	it('PowerShell 5.1 dates', () => expect(parsePsDate('/Date(1793292656000)/')?.getTime()).toBe(1793292656000))
	it('encoded commands are UTF-16LE base64', () => expect(Buffer.from(encodeCommand('hi'), 'base64').toString('utf16le')).toBe('hi'))
})

describe('on this PC', () => {
	it('a Windows binary is signed by Microsoft (false-positive guard)', async () => {
		const report = await checkSignature(join(process.env['WINDIR'] ?? 'C:\\Windows', 'explorer.exe'))
		expect(report.status).toBe('Valid')
		expect(report.signer).toContain('Microsoft')
		expect(report.isOsBinary).toBe(true)
	}, 30_000)
	// A .ps1 can carry a signature, so an unsigned one is NotSigned. A .txt
	// can't be signed at all and comes back UnknownError instead.
	it('an unsigned script is NotSigned, and a path with quotes is handled', async () => {
		const dir = await mkdtemp(join(tmpdir(), "zing sig 'q' "))
		const file = join(dir, "it's.ps1")
		await writeFile(file, "Write-Output 'hello'")
		expect((await checkSignature(file)).status).toBe('NotSigned')
	}, 30_000)
	it('writes and reads back the Mark of the Web', async () => {
		const file = join(await mkdtemp(join(tmpdir(), 'zing-motw-')), 'download.bin')
		await writeFile(file, 'x')
		expect(await readMarkOfTheWeb(file)).toBeUndefined()
		await writeMarkOfTheWeb(file, { zoneId: INTERNET_ZONE, hostUrl: 'https://a.example/download.bin' })
		expect(await readMarkOfTheWeb(file)).toMatchObject({ zoneId: 3, hostUrl: 'https://a.example/download.bin' })
	})
	it('reads Defender status', async () => {
		const status = await defenderStatus()
		expect(typeof status.realTimeProtection).toBe('boolean')
		expect(status.signaturesUpdated).toBeInstanceOf(Date)
	}, 30_000)
	it('Defender says a harmless file is clean', async () => {
		const file = join(await mkdtemp(join(tmpdir(), 'zing-scan-')), 'harmless.txt')
		await writeFile(file, 'just text')
		expect(await scanFile(file)).toBe('clean')
	}, 120_000)
	it('reads system posture without admin', async () => {
		const posture = await systemPosture()
		expect(['on', 'off', 'unknown']).toContain(posture.secureBoot)
		expect(posture.firewall.length).toBeGreaterThan(0)
	}, 30_000)
})

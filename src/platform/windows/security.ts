// Windows replacements for Krackn's macOS checks:
// Gatekeeper/notarization -> Authenticode, quarantine xattr -> Mark of the
// Web, XProtect -> Microsoft Defender, SIP/FileVault -> Secure Boot,
// BitLocker, firewall and SmartScreen. Like Krackn, this reports what the OS
// already does rather than duplicating it, and every check works without
// administrator rights.
import { existsSync } from 'node:fs'
import { readFile, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { psQuote } from '../../shared/network/dohScript'
import { runProcess } from '../process'
import { parsePsDate, runPowerShell, runPowerShellJson } from './powershell'

// ---------- Mark of the Web ----------

// Zone 3 is "Internet". Windows (SmartScreen, Office protected view) only
// warns about a downloaded file if this stream exists, so a file without it
// has skipped those checks: the equivalent of Krackn's missing-quarantine flag.
export const INTERNET_ZONE = 3

export interface MarkOfTheWeb {
	zoneId: number
	hostUrl?: string
	referrerUrl?: string
}

export function parseZoneIdentifier(text: string): MarkOfTheWeb | undefined {
	const values = new Map<string, string>()
	for (const line of text.split(/\r?\n/)) {
		const at = line.indexOf('=')
		if (at > 0) values.set(line.slice(0, at).trim().toLowerCase(), line.slice(at + 1).trim())
	}
	const zone = values.get('zoneid')
	if (zone === undefined || !/^\d+$/.test(zone)) return undefined
	return { zoneId: Number(zone), hostUrl: values.get('hosturl'), referrerUrl: values.get('referrerurl') }
}

export function formatZoneIdentifier(mark: MarkOfTheWeb): string {
	const lines = ['[ZoneTransfer]', `ZoneId=${mark.zoneId}`]
	if (mark.referrerUrl) lines.push(`ReferrerUrl=${mark.referrerUrl}`)
	if (mark.hostUrl) lines.push(`HostUrl=${mark.hostUrl}`)
	return `${lines.join('\r\n')}\r\n`
}

// The mark lives in an NTFS alternate data stream, which Node can address as
// "<file>:Zone.Identifier".
export async function readMarkOfTheWeb(path: string): Promise<MarkOfTheWeb | undefined> {
	try {
		return parseZoneIdentifier(await readFile(`${path}:Zone.Identifier`, 'utf8'))
	} catch (error) {
		if ((error as NodeJS.ErrnoException).code === 'ENOENT') return undefined
		throw error
	}
}

export async function writeMarkOfTheWeb(path: string, mark: MarkOfTheWeb): Promise<void> {
	await writeFile(`${path}:Zone.Identifier`, formatZoneIdentifier(mark), 'utf8')
}

// ---------- Authenticode ----------

export type SignatureStatus = 'Valid' | 'NotSigned' | 'HashMismatch' | 'NotTrusted' | 'UnknownError' | 'NotSupportedFileFormat' | 'Incompatible'

export interface SignatureReport {
	status: SignatureStatus
	message: string
	signer?: string
	isOsBinary: boolean
}

interface RawSignature {
	Status: string
	StatusMessage: string
	Subject: string | null
	IsOSBinary: boolean | null
}

export function parseSignature(raw: RawSignature): SignatureReport {
	return { status: raw.Status as SignatureStatus, message: raw.StatusMessage, signer: raw.Subject ?? undefined, isOsBinary: raw.IsOSBinary === true }
}

export async function checkSignature(path: string): Promise<SignatureReport> {
	const raw = await runPowerShellJson<RawSignature>(
		`Get-AuthenticodeSignature -LiteralPath ${psQuote(path)} | Select-Object @{n='Status';e={$_.Status.ToString()}}, StatusMessage, @{n='Subject';e={$_.SignerCertificate.Subject}}, IsOSBinary`,
	)
	return parseSignature(raw)
}

// ---------- Microsoft Defender ----------

export interface DefenderStatus {
	serviceEnabled: boolean
	antivirusEnabled: boolean
	realTimeProtection: boolean
	tamperProtected: boolean
	signaturesUpdated?: Date
}

interface RawDefender {
	AMServiceEnabled: boolean
	AntivirusEnabled: boolean
	RealTimeProtectionEnabled: boolean
	IsTamperProtected: boolean
	AntivirusSignatureLastUpdated: string
}

export function parseDefender(raw: RawDefender): DefenderStatus {
	return {
		serviceEnabled: raw.AMServiceEnabled,
		antivirusEnabled: raw.AntivirusEnabled,
		realTimeProtection: raw.RealTimeProtectionEnabled,
		tamperProtected: raw.IsTamperProtected,
		signaturesUpdated: parsePsDate(raw.AntivirusSignatureLastUpdated),
	}
}

export async function defenderStatus(): Promise<DefenderStatus> {
	return parseDefender(await runPowerShellJson<RawDefender>('Get-MpComputerStatus | Select-Object AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, IsTamperProtected, AntivirusSignatureLastUpdated'))
}

export type ScanVerdict = 'clean' | 'threat'

export class DefenderScanError extends Error {
	override name = 'DefenderScanError'
}

export function mpCmdRunPath(env: NodeJS.ProcessEnv = process.env): string {
	return join(env['ProgramFiles'] ?? 'C:\\Program Files', 'Windows Defender', 'MpCmdRun.exe')
}

// Defender is the signature scanner here, as XProtect was on the Mac: Zing
// asks it about one file instead of shipping its own definitions. Remediation
// is left to Defender's own real-time protection.
export async function scanFile(path: string): Promise<ScanVerdict> {
	const exe = mpCmdRunPath()
	if (!existsSync(exe)) throw new DefenderScanError(`Microsoft Defender's scanner isn't at ${exe}`)
	const result = await runProcess(exe, ['-Scan', '-ScanType', '3', '-File', path, '-DisableRemediation'], { timeoutMs: 120_000 })
	if (result.exitCode === 0) return 'clean'
	if (result.exitCode === 2) return 'threat'
	throw new DefenderScanError(`MpCmdRun exited ${result.exitCode}: ${(result.stdout + result.stderr).trim().split(/\r?\n/).at(-1) ?? ''}`)
}

// ---------- System posture ----------

export type Tristate = 'on' | 'off' | 'unknown'

export interface SystemPosture {
	secureBoot: Tristate
	bitLocker: Tristate | 'suspended'
	firewall: { profile: string; enabled: boolean }[]
	smartScreen: Tristate
}

// Shell's System.Volume.BitLockerProtection: 1 on, 2 off, 3 encrypting,
// 5 suspended. Readable without admin, unlike Get-BitLockerVolume.
export function bitLockerFromShell(value: unknown): SystemPosture['bitLocker'] {
	switch (Number(value)) {
		case 1:
		case 3:
			return 'on'
		case 2:
			return 'off'
		case 5:
			return 'suspended'
		default:
			return 'unknown'
	}
}

export function smartScreenFromRegistry(policy: unknown, explorer: unknown): Tristate {
	if (policy === 0) return 'off'
	if (policy === 1) return 'on'
	if (typeof explorer === 'string') return explorer.toLowerCase() === 'off' ? 'off' : 'on'
	return 'unknown'
}

interface RawPosture {
	SecureBoot: number | null
	BitLocker: number | string | null
	Firewall: { Name: string; Enabled: number | boolean }[] | { Name: string; Enabled: number | boolean }
	SmartScreenPolicy: number | null
	SmartScreenExplorer: string | null
}

export function parsePosture(raw: RawPosture): SystemPosture {
	const firewall = Array.isArray(raw.Firewall) ? raw.Firewall : [raw.Firewall]
	return {
		secureBoot: raw.SecureBoot === 1 ? 'on' : raw.SecureBoot === 0 ? 'off' : 'unknown',
		bitLocker: bitLockerFromShell(raw.BitLocker),
		firewall: firewall.map((f) => ({ profile: f.Name, enabled: f.Enabled === true || f.Enabled === 1 })),
		smartScreen: smartScreenFromRegistry(raw.SmartScreenPolicy, raw.SmartScreenExplorer),
	}
}

const POSTURE_SCRIPT = `
$sb = (Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\SecureBoot\\State' -ErrorAction SilentlyContinue).UEFISecureBootEnabled
$bl = (New-Object -ComObject Shell.Application).NameSpace($env:SystemDrive + '\\').Self.ExtendedProperty('System.Volume.BitLockerProtection')
$fw = Get-NetFirewallProfile | Select-Object Name, @{n='Enabled';e={[bool]$_.Enabled}}
$ssp = (Get-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\System' -ErrorAction SilentlyContinue).EnableSmartScreen
$sse = (Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer' -ErrorAction SilentlyContinue).SmartScreenEnabled
[pscustomobject]@{ SecureBoot = $sb; BitLocker = $bl; Firewall = @($fw); SmartScreenPolicy = $ssp; SmartScreenExplorer = $sse }`

export async function systemPosture(): Promise<SystemPosture> {
	return parsePosture(await runPowerShellJson<RawPosture>(POSTURE_SCRIPT))
}

export { runPowerShell }

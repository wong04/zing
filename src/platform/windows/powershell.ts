import { runProcess, type ProcessResult } from '../process'

export class PowerShellError extends Error {
	override name = 'PowerShellError'
	constructor(
		message: string,
		readonly result: ProcessResult,
	) {
		super(message)
	}
}

// -EncodedCommand takes UTF-16LE base64, so no quoting layer between Node and
// PowerShell can mangle the script. Values that come from outside (paths)
// must still be embedded with psQuote.
export function encodeCommand(script: string): string {
	return Buffer.from(script, 'utf16le').toString('base64')
}

// A PSModulePath inherited from PowerShell 7 (a pwsh terminal, GitHub's
// runners) lists 7's modules first, and Windows PowerShell 5.1 then fails to
// autoload its own built-ins such as Get-AuthenticodeSignature. Without the
// variable, 5.1 rebuilds its default module path.
export function windowsPowerShellEnv(env: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
	return Object.fromEntries(Object.entries(env).filter(([key]) => key.toLowerCase() !== 'psmodulepath'))
}

export async function runPowerShell(script: string, timeoutMs = 30_000): Promise<string> {
	const args = ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encodeCommand(script)]
	const result = await runProcess('powershell.exe', args, { timeoutMs, env: windowsPowerShellEnv(process.env) })
	if (result.exitCode !== 0) throw new PowerShellError(`PowerShell exited ${result.exitCode}: ${result.stderr.trim()}`, result)
	return result.stdout
}

export async function runPowerShellJson<T>(script: string, timeoutMs?: number): Promise<T> {
	const out = await runPowerShell(`${script} | ConvertTo-Json -Depth 4 -Compress`, timeoutMs)
	return JSON.parse(out) as T
}

// Windows PowerShell 5.1 serialises dates as "/Date(1793292656000)/".
export function parsePsDate(value: unknown): Date | undefined {
	if (typeof value !== 'string') return undefined
	const match = /\/Date\((-?\d+)\)\//.exec(value)
	if (match) return new Date(Number(match[1]))
	const parsed = Date.parse(value)
	return Number.isNaN(parsed) ? undefined : new Date(parsed)
}

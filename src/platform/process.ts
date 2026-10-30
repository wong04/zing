// Runs external tools (ffmpeg, yt-dlp, pandoc, PowerShell). Replaces Krackn's
// ShellRunner.swift. Arguments are always an array passed straight to the
// process, never through a shell, so file names can't inject commands.
import { spawn } from 'node:child_process'
import { access, constants } from 'node:fs/promises'
import { delimiter, isAbsolute, join } from 'node:path'

export interface ProcessResult {
	exitCode: number
	stdout: string
	stderr: string
}

export class ToolNotFoundError extends Error {
	override name = 'ToolNotFoundError'
	constructor(readonly tool: string) {
		super(`${tool} isn't installed.`)
	}
}

export class ProcessCancelledError extends Error {
	override name = 'ProcessCancelledError'
}

export class ProcessTimeoutError extends Error {
	override name = 'ProcessTimeoutError'
}

// Splits a stream into lines. Both \n and \r count as terminators because
// ffmpeg and yt-dlp redraw progress with carriage returns.
export class LineSplitter {
	#partial = ''

	push(chunk: string): string[] {
		this.#partial += chunk
		const parts = this.#partial.split(/\r\n|\r|\n/)
		this.#partial = parts.pop() ?? ''
		return parts.map((l) => l.trim()).filter(Boolean)
	}

	flush(): string[] {
		const rest = this.#partial.trim()
		this.#partial = ''
		return rest ? [rest] : []
	}
}

// winget puts command shims in WinGet\Links, but a running app keeps the PATH
// it started with, so a tool installed a minute ago isn't on it yet. Search
// Zing's own bin folder and the winget folders explicitly, the way Krackn
// searched Homebrew's folders because a Finder-launched app gets a bare PATH.
export function searchPaths(env: NodeJS.ProcessEnv, zingBin: string): string[] {
	const local = env['LOCALAPPDATA'] ?? ''
	const fromPath = (env['PATH'] ?? env['Path'] ?? '').split(delimiter).filter(Boolean)
	return [zingBin, local && join(local, 'Microsoft', 'WinGet', 'Links'), ...fromPath].filter(Boolean)
}

export function executableNames(tool: string, env: NodeJS.ProcessEnv): string[] {
	if (/\.[a-z0-9]+$/i.test(tool)) return [tool]
	const extensions = (env['PATHEXT'] ?? '.COM;.EXE;.BAT;.CMD').split(';').filter(Boolean)
	return extensions.map((ext) => tool + ext.toLowerCase())
}

export async function which(tool: string, dirs: readonly string[], env: NodeJS.ProcessEnv = process.env): Promise<string | undefined> {
	if (isAbsolute(tool)) return (await isFile(tool)) ? tool : undefined
	for (const dir of dirs) {
		for (const name of executableNames(tool, env)) {
			const candidate = join(dir, name)
			if (await isFile(candidate)) return candidate
		}
	}
	return undefined
}

async function isFile(path: string): Promise<boolean> {
	try {
		await access(path, constants.F_OK)
		return true
	} catch {
		return false
	}
}

export interface RunOptions {
	cwd?: string
	onLine?: (line: string) => void
	signal?: AbortSignal
	timeoutMs?: number
}

// Resolves with the exit code even when it's non-zero: a failed conversion
// is a result to explain, not an exception. Rejects only when the process
// couldn't start, was cancelled, or timed out.
export function runProcess(executable: string, args: readonly string[], options: RunOptions = {}): Promise<ProcessResult> {
	return new Promise((resolve, reject) => {
		if (options.signal?.aborted) {
			reject(new ProcessCancelledError(`${executable} was cancelled before it started`))
			return
		}
		const child = spawn(executable, args, { cwd: options.cwd, windowsHide: true, shell: false })
		const out: Buffer[] = []
		const err: Buffer[] = []
		const lines = new LineSplitter()
		let settled = false
		const finish = (action: () => void) => {
			if (settled) return
			settled = true
			clearTimeout(timer)
			options.signal?.removeEventListener('abort', onAbort)
			action()
		}
		const onAbort = () => {
			child.kill()
			finish(() => reject(new ProcessCancelledError(`${executable} was cancelled`)))
		}
		const timer = options.timeoutMs
			? setTimeout(() => {
					child.kill()
					finish(() => reject(new ProcessTimeoutError(`${executable} took longer than ${options.timeoutMs} ms`)))
				}, options.timeoutMs)
			: undefined
		options.signal?.addEventListener('abort', onAbort)
		child.stdout.on('data', (chunk: Buffer) => {
			out.push(chunk)
			if (options.onLine) lines.push(chunk.toString('utf8')).forEach(options.onLine)
		})
		child.stderr.on('data', (chunk: Buffer) => err.push(chunk))
		child.on('error', (error: NodeJS.ErrnoException) => {
			finish(() => reject(error.code === 'ENOENT' ? new ToolNotFoundError(executable) : error))
		})
		child.on('close', (code) => {
			if (options.onLine) lines.flush().forEach(options.onLine)
			finish(() => resolve({ exitCode: code ?? -1, stdout: Buffer.concat(out).toString('utf8'), stderr: Buffer.concat(err).toString('utf8') }))
		})
	})
}

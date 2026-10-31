// Integration tests against real Windows processes, in the spirit of
// Krackn's `cancellation` suite: a cancelled run must return promptly, and
// many simultaneous cancels must not deadlock.
import { mkdtemp, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { executableNames, LineSplitter, ProcessCancelledError, ProcessTimeoutError, runProcess, searchPaths, ToolNotFoundError, which } from './process'

// Node, not PowerShell: Windows PowerShell can take over 5 s to cold-start on a
// busy CI runner, which timed this suite out. These tests are about the runner.
const NODE = process.execPath
const node = (script: string) => ['-e', script]

describe('line splitting', () => {
	it('splits on \\n, \\r\\n and bare \\r (progress redraws)', () => {
		const s = new LineSplitter()
		expect(s.push('a\nb\r\nc\r50%\r6')).toEqual(['a', 'b', 'c', '50%'])
		expect(s.push('0%\n')).toEqual(['60%'])
		expect(s.flush()).toEqual([])
	})
	it('keeps a partial line until it completes or flushes', () => {
		const s = new LineSplitter()
		expect(s.push('par')).toEqual([])
		expect(s.flush()).toEqual(['par'])
	})
})

describe('finding tools', () => {
	it('searches Zing bin, then winget Links, then PATH', () => {
		expect(searchPaths({ LOCALAPPDATA: 'C:\\L', PATH: 'C:\\a;C:\\b' }, 'C:\\Z\\bin')).toEqual(['C:\\Z\\bin', 'C:\\L\\Microsoft\\WinGet\\Links', 'C:\\a', 'C:\\b'])
	})
	it('tries PATHEXT extensions unless one is given', () => {
		expect(executableNames('ffmpeg', { PATHEXT: '.EXE;.CMD' })).toEqual(['ffmpeg.exe', 'ffmpeg.cmd'])
		expect(executableNames('yt-dlp.exe', {})).toEqual(['yt-dlp.exe'])
	})
	it('finds a tool in a searched folder', async () => {
		const dir = await mkdtemp(join(tmpdir(), 'zing-which-'))
		await writeFile(join(dir, 'fake-tool.exe'), '')
		expect(await which('fake-tool', [dir], { PATHEXT: '.EXE' })).toBe(join(dir, 'fake-tool.exe'))
		expect(await which('missing-tool', [dir], { PATHEXT: '.EXE' })).toBeUndefined()
	})
})

describe('running processes', () => {
	it('captures stdout, stderr and the exit code without throwing on failure', async () => {
		const result = await runProcess(NODE, node("process.stdout.write('hi'); process.stderr.write('oops'); process.exit(3)"))
		expect(result).toEqual({ exitCode: 3, stdout: 'hi', stderr: 'oops' })
	})
	it('passes arguments without a shell, so metacharacters stay literal', async () => {
		const tricky = 'a & echo pwned | b'
		const result = await runProcess(process.execPath, ['-e', 'process.stdout.write(process.argv[1])', tricky])
		expect(result.stdout).toBe(tricky)
	})
	it('streams lines as they arrive', async () => {
		const lines: string[] = []
		await runProcess(NODE, node("console.log('one'); console.log('two')"), { onLine: (l) => lines.push(l) })
		expect(lines).toEqual(['one', 'two'])
	})
	it('a missing executable is a ToolNotFoundError', async () => {
		await expect(runProcess('zing-definitely-missing-tool.exe', [])).rejects.toBeInstanceOf(ToolNotFoundError)
	})
	it('cancelling returns in well under the run time', async () => {
		const controller = new AbortController()
		const started = Date.now()
		const run = runProcess(NODE, node('setTimeout(() => {}, 30_000)'), { signal: controller.signal })
		setTimeout(() => controller.abort(), 300)
		await expect(run).rejects.toBeInstanceOf(ProcessCancelledError)
		expect(Date.now() - started).toBeLessThan(5000)
	}, 15_000)
	it('15 simultaneous cancels do not deadlock', async () => {
		const controller = new AbortController()
		const runs = Array.from({ length: 15 }, () => runProcess(NODE, node('setTimeout(() => {}, 30_000)'), { signal: controller.signal }))
		setTimeout(() => controller.abort(), 500)
		const results = await Promise.allSettled(runs)
		expect(results.every((r) => r.status === 'rejected' && r.reason instanceof ProcessCancelledError)).toBe(true)
	}, 30_000)
	it('times out', async () => {
		await expect(runProcess(NODE, node('setTimeout(() => {}, 30_000)'), { timeoutMs: 500 })).rejects.toBeInstanceOf(ProcessTimeoutError)
	}, 15_000)
})

import { spawnSync } from 'node:child_process'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

const SCRIPT = join(import.meta.dirname, 'next-version.ps1')

let folder: string | undefined

afterEach(() => {
	if (folder) rmSync(folder, { recursive: true, force: true })
	folder = undefined
})

function releasesWith(...files: string[]): string {
	folder = mkdtempSync(join(tmpdir(), 'zing-releases-'))
	for (const file of files) writeFileSync(join(folder, file), '')
	return folder
}

function nextVersion(releases: string): { status: number | null; stdout: string; stderr: string } {
	const run = spawnSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', SCRIPT, '-Releases', releases], { encoding: 'utf8' })
	return { status: run.status, stdout: run.stdout.trim(), stderr: run.stderr }
}

describe.runIf(process.platform === 'win32')('next-version.ps1', () => {
	it('bumps the patch of the highest full package, compared as versions, not text', () => {
		const releases = releasesWith('ZingBrowser-0.9.2-full.nupkg', 'ZingBrowser-0.9.10-full.nupkg', 'ZingBrowser-0.9.9-full.nupkg', 'ZingBrowser-win-Setup.exe', 'releases.win.json')
		expect(nextVersion(releases)).toMatchObject({ status: 0, stdout: '0.9.11' })
	})

	it('ignores delta packages and other apps’ packages', () => {
		const releases = releasesWith('ZingBrowser-0.9.3-full.nupkg', 'ZingBrowser-0.9.4-delta.nupkg', 'Other-2.0.0-full.nupkg')
		expect(nextVersion(releases)).toMatchObject({ status: 0, stdout: '0.9.4' })
	})

	it('fails, saying what to do, when there is no earlier release to follow', () => {
		const run = nextVersion(releasesWith('releases.win.json'))
		expect(run.status).not.toBe(0)
		expect(run.stderr).toContain('npm run pack -- -Version')
	})
})

// GitHub's Windows runners (and anyone launching Zing from a PowerShell 7
// terminal) hand Windows PowerShell 5.1 a PSModulePath that lists PowerShell
// 7's modules first. 5.1 then autoloads 7's copy of a built-in module, fails
// to load it, and reports the cmdlet as missing. This reproduces that with a
// broken module of the same name.
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { runPowerShellJson } from './powershell'

const original = process.env['PSModulePath']

afterEach(() => {
	if (original === undefined) delete process.env['PSModulePath']
	else process.env['PSModulePath'] = original
})

describe('Windows PowerShell module path', () => {
	it('ignores a PowerShell 7 module path inherited from the parent process', async () => {
		const modules = await mkdtemp(join(tmpdir(), 'zing-psmodules-'))
		const broken = join(modules, 'Microsoft.PowerShell.Security')
		await mkdir(broken)
		await writeFile(join(broken, 'Microsoft.PowerShell.Security.psd1'), "@{ RootModule = 'missing.dll'; ModuleVersion = '7.0.0'; CmdletsToExport = 'Get-AuthenticodeSignature' }")
		process.env['PSModulePath'] = `${modules};${original ?? ''}`
		const status = await runPowerShellJson<string>(`(Get-AuthenticodeSignature -LiteralPath (Join-Path $env:WINDIR 'explorer.exe')).Status.ToString()`)
		expect(status).toBe('Valid')
	}, 30_000)
})

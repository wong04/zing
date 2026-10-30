import { describe, expect, it } from 'vitest'
import { searchPaths, which } from '../process'
import { parseVersion, toolVersion, TOOLS, wingetInstallArgs, wingetUpgradeArgs } from './tools'

describe('winget arguments', () => {
	it('installs by exact id from the winget source', () => {
		expect(wingetInstallArgs(TOOLS.ffmpeg)).toEqual(['install', '--id', 'Gyan.FFmpeg', '--exact', '--source', 'winget'])
	})
	it('never accepts licence agreements on the user’s behalf', () => {
		for (const tool of Object.values(TOOLS)) expect(wingetInstallArgs(tool).join(' ')).not.toMatch(/accept/)
	})
	it('upgrades yt-dlp in one step', () => expect(wingetUpgradeArgs(TOOLS['yt-dlp'])[0]).toBe('upgrade'))
})

describe('version parsing', () => {
	it.each([
		['ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024', '7.1-full_build-www.gyan.dev'],
		['2025.10.22\n', '2025.10.22'],
		['Version: ImageMagick 7.1.1-38 Q16-HDRI x64', '7.1.1-38'],
		['pandoc 3.5\nFeatures: +server +lua', '3.5'],
		['uv 0.9.5 (d5f39331a 2025-10-21)', '0.9.5'],
	])('%s', (output, expected) => expect(parseVersion(output)).toBe(expected))
	it('empty output has no version', () => expect(parseVersion('\n')).toBeUndefined())
})

describe('on this PC', () => {
	it('finds uv if installed and reads its version', async () => {
		const path = await which('uv', searchPaths(process.env, ''))
		if (!path) return
		expect(await toolVersion(path, TOOLS.uv)).toMatch(/^\d+\.\d+/)
	})
})

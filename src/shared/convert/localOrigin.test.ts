import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

const root = join(import.meta.dirname, '..', '..', '..')
const read = (path: string): string => readFileSync(join(root, path), 'utf8')

describe('the name the host answers file requests on', () => {
	it('is the same in the worker protocol and in the host', () => {
		const host = /LocalFilesHost = "([^"]+)"/.exec(read('host/Zing.Core/ZingOrigin.cs'))![1]
		const renderer = /LOCAL_ORIGIN = 'https:\/\/([^']+)'/.exec(read('src/renderer/src/tools/files/bgremoveProtocol.ts'))![1]
		expect(renderer).toBe(host)
	})
	it('serves the same two shapes the tool asks for', () => {
		const tool = read('src/renderer/src/tools/files/BgRemoveTool.tsx')
		expect(tool).toContain('/zing-local/model/')
		expect(tool).toContain('/zing-local/file/')
		expect(read('host/Zing.Core/Engines/LocalFiles.cs')).toContain('Prefix = "/zing-local/"')
	})
	it('leaves the chrome content security policy as it was: nothing is added for the model', () => {
		const html = read('src/renderer/index.html')
		expect(html).toContain(`content="default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'"`)
		expect(html).not.toMatch(/wasm-unsafe-eval|unsafe-eval|connect-src|script-src/)
	})
})

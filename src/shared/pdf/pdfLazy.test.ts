import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative, sep } from 'node:path'
import { describe, expect, it } from 'vitest'

const root = join(import.meta.dirname, '..', '..')

function sources(dir: string): string[] {
	return readdirSync(dir).flatMap((name) => {
		const path = join(dir, name)
		if (statSync(path).isDirectory()) return sources(path)
		return /\.(ts|tsx)$/.test(name) && !/\.test\.ts$/.test(name) ? [path] : []
	})
}

const importers = (pattern: RegExp): string[] =>
	sources(root)
		.filter((file) => pattern.test(readFileSync(file, 'utf8')))
		.map((file) => relative(root, file).split(sep).join('/'))
		.sort()

const render = (): string => readFileSync(join(root, 'renderer', 'src', 'tools', 'files', 'pdfRender.ts'), 'utf8')

describe('pdf-lib and pdf.js load only when a PDF tool opens', () => {
	it('names pdf-lib in one module, which only the worker imports', () => {
		expect(importers(/from 'pdf-lib'/)).toEqual(['shared/pdf/pdfEdit.ts'])
		expect(importers(/shared\/pdf\/pdfEdit'/)).toEqual(['renderer/src/tools/files/pdfEdit.worker.ts'])
	})
	it('names pdf.js in one file, and only in dynamic imports', () => {
		expect(importers(/pdfjs-dist/)).toEqual(['renderer/src/tools/files/pdfRender.ts'])
		expect(render()).not.toMatch(/^import [^t].*pdfjs-dist/m)
		expect(render()).toMatch(/= import\('pdfjs-dist'\)/)
	})
})

describe('how pdf.js opens a PDF', () => {
	it('runs no script, fetches nothing, and uses no eval', () => {
		const options = /OPEN_OPTIONS = \{([^}]*)\}/.exec(render())![1]!
		for (const setting of ['isEvalSupported: false', 'enableXfa: false', 'disableAutoFetch: true', 'disableStream: true', 'disableRange: true', 'disableFontFace: true']) expect(options).toContain(setting)
	})
	it('names no address outside the bundle and no wasm', () => {
		const code = render()
			.split('\n')
			.filter((line) => !/^\s*(\*|\/\/|\/\*\*)/.test(line))
			.join('\n')
		expect(code).not.toMatch(/wasmUrl|iccUrl|https?:|enableScripting|\burl:|sandbox/)
		expect(code).toMatch(/cMapUrl: bundleFolder/)
		expect(code).toMatch(/standardFontDataUrl: bundleFolder/)
	})
})

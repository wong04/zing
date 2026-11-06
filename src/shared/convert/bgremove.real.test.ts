// Runs the real IS-Net model on a drawn fixture, in Node's WebAssembly (the same ONNX Runtime build the browser uses).
// Skipped unless ZING_REAL_MODEL points at a folder: the model is fetched from the catalog's own URL into it, outside
// the repo, and checked against the catalog's SHA-256. Run by hand; not in CI.
import { createHash } from 'node:crypto'
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import * as ort from 'onnxruntime-web'
import { describe, expect, it } from 'vitest'
import { normaliseMask, resizeMask } from './alphaMask'
import { drawFixture, scoreMask } from './bgFixture.node'
import { MODEL_SIZE, toModelInput, type Normalisation } from './bgPrep'

const folder = process.env.ZING_REAL_MODEL
// The scores go to a file beside the model, because the runner hides a passing test's console output.
const note = (...parts: unknown[]): void => appendFileSync(join(folder!, 'real-scores.log'), parts.map((p) => (typeof p === 'string' ? p : JSON.stringify(p))).join(' ') + String.fromCharCode(10))
const catalog = readFileSync(join(import.meta.dirname, '..', '..', '..', 'host', 'Zing.Core', 'Engines', 'EngineCatalog.cs'), 'utf8')
const section = catalog.slice(catalog.indexOf('EngineIds.BackgroundModel,'))
const url = /new Uri\("([^"]+)"\)/.exec(section)![1]!
const sha256 = /"([0-9a-f]{64})"/.exec(section)![1]!

async function model(): Promise<Uint8Array> {
	const path = join(folder!, 'isnet-general-use.onnx')
	if (!existsSync(path)) {
		mkdirSync(folder!, { recursive: true })
		writeFileSync(path, new Uint8Array(await (await fetch(url)).arrayBuffer()))
	}
	return readFileSync(path)
}

function resizeRgba(rgba: Uint8ClampedArray, w: number, h: number, to: number): Uint8ClampedArray {
	const out = new Uint8ClampedArray(to * to * 4)
	for (let c = 0; c < 4; c++) {
		const plane = new Float32Array(w * h)
		for (let i = 0; i < w * h; i++) plane[i] = rgba[i * 4 + c]!
		const resized = resizeMask(plane, w, h, to, to)
		for (let i = 0; i < to * to; i++) out[i * 4 + c] = resized[i]!
	}
	return out
}

describe.skipIf(!folder)('the real model on a drawn fixture', () => {
	it('is the file the catalog pins', async () => {
		expect(createHash('sha256').update(await model()).digest('hex')).toBe(sha256)
	})

	const variants: Record<string, Normalisation> = {
		'mean 0.5, std 1': { mean: [0.5, 0.5, 0.5], std: [1, 1, 1], divideByMax: false },
		'rembg: divide by max, ImageNet means, std 1': { mean: [0.485, 0.456, 0.406], std: [1, 1, 1], divideByMax: true },
		'ImageNet mean and std': { mean: [0.485, 0.456, 0.406], std: [0.229, 0.224, 0.225], divideByMax: false },
	}

	it('separates the subject from the background under each normalisation (prints the scores)', async () => {
		ort.env.wasm.numThreads = 1
		const started = performance.now()
		const session = await ort.InferenceSession.create(await model(), { executionProviders: ['wasm'] })
		note('inputs', session.inputNames, 'outputs', session.outputNames, 'load ms', Math.round(performance.now() - started))
		const fixture = drawFixture(800, 600)
		const input = resizeRgba(fixture.rgba, fixture.width, fixture.height, MODEL_SIZE)
		for (const [name, norm] of Object.entries(variants)) {
			const t0 = performance.now()
			const result = await session.run({ [session.inputNames[0]!]: new ort.Tensor('float32', toModelInput(input, MODEL_SIZE, MODEL_SIZE, norm), [1, 3, MODEL_SIZE, MODEL_SIZE]) })
			const raw = result[session.outputNames[0]!]!
			const values = raw.data as Float32Array
			let low = Infinity
			let high = -Infinity
			for (const v of values) {
				low = Math.min(low, v)
				high = Math.max(high, v)
			}
			const mask = resizeMask(normaliseMask(values), MODEL_SIZE, MODEL_SIZE, fixture.width, fixture.height)
			const score = scoreMask(mask, fixture.truth)
			note(name, 'dims', raw.dims, 'raw range', low.toFixed(3), high.toFixed(3), 'inside', score.inside.toFixed(3), 'outside', score.outside.toFixed(3), 'ms', Math.round(performance.now() - t0), 'rss MB', Math.round(process.memoryUsage().rss / 1048576))
		}
		await session.release()
	}, 600_000)
})

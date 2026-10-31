// Port of the pure parts of Krackn Tests/drop. Krackn's suite drove real
// NSItemProviders and the network; here the same cases go through the
// planning and naming the main process uses. Skipped as AppKit-only: the two
// `UTType.item` registration checks and "downloaded non-empty" (network).
// The data-shaped cases live in dropRouter.vectors.json, which the C# port (DropRouterTests.cs) reads too.
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { panelContentFromId } from '../toolbox/panelContent'
import {
	acceptedDragTypes,
	acceptsDrag,
	acceptsFiles,
	asFilePath,
	asWebURL,
	detectExtension,
	hoverDragTypes,
	nameForDownload,
	nameForDroppedImage,
	noticesDrag,
	pathExtension,
	planDrop,
	safeFileName,
	uniqueName,
	type DropPayload,
	type DropPlan,
} from './dropRouter'

interface Bytes {
	readonly hex: string
	readonly zeros: number
}

interface PlanCase {
	readonly name: string
	readonly payload: {
		fileLocation?: string
		image?: Bytes
		url?: string
		suggestedName?: string
		otherTypes?: string[]
	}
	readonly want:
		| { kind: 'open'; path: string }
		| { kind: 'write'; name: string; bytes: Bytes }
		| { kind: 'download'; url: string }
		| { kind: 'materialise'; types: readonly string[] }
		| null
}

const vectors = JSON.parse(readFileSync(new URL('./dropRouter.vectors.json', import.meta.url), 'utf8')) as {
	plans: PlanCase[]
	droppedNames: { name: string; bytes: Bytes; suggestedName: string | null; want: string }[]
	downloadNames: { name: string; url: string; mime: string | null; bytes: Bytes; want: string }[]
	sniff: { name: string; bytes: Bytes; want: string | null }[]
	uniqueNames: { name: string; file: string; taken: string[]; want: string }[]
	extensions: { name: string; file: string; want: string }[]
	filePaths: { name: string; location: string; want: string | null }[]
	webUrls: { name: string; item: string; want: string | null }[]
	safeNames: { name: string; file: string; want: string }[]
	targets: { name: string; content: string; want: boolean }[]
	dragTypes: { name: string; types: string[]; accepts: boolean; notices: boolean }[]
}

const now = new Date('2025-07-29T14:40:00Z')

function bytes(b: Bytes): Uint8Array {
	const head = b.hex === '' ? [] : (b.hex.match(/../g) ?? []).map((h) => parseInt(h, 16))
	return new Uint8Array([...head, ...new Array<number>(b.zeros).fill(0)])
}

function toPayload(p: PlanCase['payload']): DropPayload {
	return {
		...(p.fileLocation !== undefined && { fileLocation: p.fileLocation }),
		...(p.image !== undefined && { image: bytes(p.image) }),
		...(p.url !== undefined && { url: p.url }),
		...(p.suggestedName !== undefined && { suggestedName: p.suggestedName }),
		...(p.otherTypes !== undefined && { otherTypes: p.otherTypes }),
	}
}

function describePlan(plan: DropPlan | null): PlanCase['want'] {
	if (plan === null) return null
	if (plan.kind === 'write') {
		const hex = [...plan.bytes].map((b) => b.toString(16).padStart(2, '0')).join('')
		return { kind: 'write', name: plan.name, bytes: { hex, zeros: 0 } }
	}
	return plan
}

describe('planDrop (shared vectors)', () => {
	it.each(vectors.plans.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		const got = describePlan(planDrop(toPayload(c.payload), now))
		if (c.want?.kind === 'write' && got?.kind === 'write') {
			// The vector's bytes are hex plus zeros; compare as the same bytes.
			expect(bytes({ hex: got.bytes.hex, zeros: 0 })).toEqual(bytes(c.want.bytes))
			expect(got.name).toBe(c.want.name)
			return
		}
		expect(got).toEqual(c.want)
	})
})

describe('names and locations (shared vectors)', () => {
	it.each(vectors.droppedNames.map((c) => [c.name, c] as const))('dropped image: %s', (_name, c) => {
		expect(nameForDroppedImage(bytes(c.bytes), c.suggestedName ?? undefined, now)).toBe(c.want)
	})
	it.each(vectors.downloadNames.map((c) => [c.name, c] as const))('download name: %s', (_name, c) => {
		expect(nameForDownload(c.url, c.mime, bytes(c.bytes))).toBe(c.want)
	})
	it.each(vectors.sniff.map((c) => [c.name, c] as const))('sniff: %s', (_name, c) => {
		expect(detectExtension(bytes(c.bytes))).toBe(c.want)
	})
	it.each(vectors.uniqueNames.map((c) => [c.name, c] as const))('unique name: %s', (_name, c) => {
		const taken = new Set(c.taken)
		expect(uniqueName(c.file, (n) => taken.has(n))).toBe(c.want)
	})
	it.each(vectors.extensions.map((c) => [c.name, c] as const))('extension: %s', (_name, c) => {
		expect(pathExtension(c.file)).toBe(c.want)
	})
	it.each(vectors.filePaths.map((c) => [c.name, c] as const))('file path: %s', (_name, c) => {
		expect(asFilePath(c.location)).toBe(c.want)
	})
	it.each(vectors.webUrls.map((c) => [c.name, c] as const))('web URL: %s', (_name, c) => {
		expect(asWebURL(c.item)).toBe(c.want)
	})
	it.each(vectors.safeNames.map((c) => [c.name, c] as const))('safe name: %s', (_name, c) => {
		expect(safeFileName(c.file)).toBe(c.want)
	})
})

describe('targets (shared vectors)', () => {
	it.each(vectors.targets.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		const content = panelContentFromId(c.content)
		if (!content) throw new TypeError(`bad content ${c.content}`)
		expect(acceptsFiles(content)).toBe(c.want)
	})
	it.each(vectors.dragTypes.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		expect(acceptsDrag(c.types)).toBe(c.accepts)
		expect(noticesDrag(c.types)).toBe(c.notices)
	})
	it('hover targets stay narrower than drop targets', () => {
		expect(hoverDragTypes.every((type) => acceptedDragTypes.includes(type))).toBe(true)
	})
})

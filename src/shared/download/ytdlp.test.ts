// Ported from reference/krackn/Tests/unit/main.swift, "L. yt-dlp failures" (14 checks).
// The cases live in ytdlp.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { explainFailure, looksLikeExtractorBreakage, parseProgress, retryArgs, ytdlpArgs, type MediaMode } from './ytdlp'
import vectors from './ytdlp.vectors.json'

describe('stale extractor is recognised, ordinary refusals are not', () => {
	it.each(vectors.breakage)('$stderr', ({ stderr, expected }) => expect(looksLikeExtractorBreakage(stderr)).toBe(expected))
})

describe('the message the user reads', () => {
	it.each(vectors.explainContains)('$name', ({ stderr, exitCode, contains }) => expect(explainFailure(stderr, exitCode)).toContain(contains))
	it.each(vectors.explainEquals)('$name', ({ stderr, exitCode, expected }) => expect(explainFailure(stderr, exitCode)).toBe(expected))
})

describe('arguments', () => {
	it.each(vectors.args)('$name', ({ mode, template, url, expected }) => {
		expect(ytdlpArgs(mode as MediaMode, template, url)).toEqual(expected)
	})
	it.each(vectors.retry)('$name', ({ args, expected }) => expect(retryArgs(args)).toEqual(expected))
})

describe('progress lines (new in Zing)', () => {
	it.each(vectors.progress)('$name', ({ line, expected }) => {
		expect(parseProgress(line)).toEqual(expected ?? undefined)
	})
})

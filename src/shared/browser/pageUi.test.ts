// pageUi.ts is mostly a shape; these vectors pin that shape and its one derivation.
// The same cases run against host/Zing.Core/PageUi.cs (PageUiTests.cs), which also round-trips each state.
import { describe, expect, it } from 'vitest'
import { hasVisiblePageUi, type PageUiState } from './pageUi'
import vectors from './pageUi.vectors.json'

describe('page UI vectors', () => {
	it.each(vectors.states)('$name', ({ state, visible }) => {
		expect(hasVisiblePageUi(state as PageUiState)).toBe(visible)
	})
})

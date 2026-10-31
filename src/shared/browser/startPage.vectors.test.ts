// The start page's exact output, shared with the C# port's tests. `npm run gen:start-page-vectors`
// rewrites startPage.vectors.json after an intended change; otherwise any difference fails here.
import { describe, expect, it, vi } from 'vitest'
import { startPageVectors } from '../../../scripts/start-page-vectors'

vi.mock('electron', () => ({ protocol: {} }))

const main = await import('../../main/start-page')

describe('start page vectors', () => {
	it('match the committed startPage.vectors.json', async () => {
		await expect(`${JSON.stringify(startPageVectors(main), null, '\t')}\n`).toMatchFileSnapshot('./startPage.vectors.json')
	})
})

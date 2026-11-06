import { afterEach, describe, expect, it } from 'vitest'
import { beginToolDrag, droppedToolFile, endToolDrag, isToolDrag, TOOL_FILE_TYPE } from './toolDrag'

function transfer() {
	const data = new Map<string, string>()
	return {
		effectAllowed: 'none',
		get types() {
			return [...data.keys()]
		},
		setData: (type: string, value: string) => void data.set(type, value),
		getData: (type: string) => data.get(type) ?? '',
	}
}

const file = { token: 'a'.repeat(32), name: 'book.pdf' }

afterEach(endToolDrag)

describe('a file dragged from one tool to another', () => {
	it('is offered as a copy under a type of its own, carrying the token and never a path', () => {
		const out = transfer()
		beginToolDrag(out, file)

		expect(out.types).toContain(TOOL_FILE_TYPE)
		expect(out.getData(TOOL_FILE_TYPE)).toBe(file.token)
		expect(out.effectAllowed).toBe('copy')
	})

	it('is a tool drag only while one of our own files is being dragged', () => {
		const out = transfer()
		expect(isToolDrag([TOOL_FILE_TYPE])).toBe(false)
		beginToolDrag(out, file)
		expect(isToolDrag(out.types)).toBe(true)
		expect(isToolDrag(['text/plain'])).toBe(false)
		expect(isToolDrag(['Files'])).toBe(false)
	})

	it('drops as the file that was picked up', () => {
		const out = transfer()
		beginToolDrag(out, file)

		expect(droppedToolFile(out)).toEqual(file)
	})

	it('ignores a token that is not the one being dragged, as a web page could offer', () => {
		beginToolDrag(transfer(), file)
		const forged = transfer()
		forged.setData(TOOL_FILE_TYPE, 'b'.repeat(32))

		expect(droppedToolFile(forged)).toBeUndefined()
	})

	it('ignores the type when no drag of ours is going on', () => {
		const forged = transfer()
		forged.setData(TOOL_FILE_TYPE, file.token)

		expect(isToolDrag(forged.types)).toBe(false)
		expect(droppedToolFile(forged)).toBeUndefined()
	})

	it('ends with the drag, so a later forged drop finds nothing', () => {
		const out = transfer()
		beginToolDrag(out, file)
		endToolDrag()

		expect(droppedToolFile(out)).toBeUndefined()
	})
})

import { afterEach, describe, expect, it } from 'vitest'
import { beginToolDrag, droppedToolFile, endToolDrag, isToolDrag, TOOL_FILE_TYPE, watchDragEnd } from './toolDrag'

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

afterEach(() => endToolDrag())

describe('a file dragged from one tool to another', () => {
	it('is offered as a copy under a type of its own, and the transfer never carries the token', () => {
		const out = transfer()
		beginToolDrag(out, file)

		expect(out.types).toContain(TOOL_FILE_TYPE)
		expect(out.effectAllowed).toBe('copy')
		for (const type of out.types) expect(out.getData(type)).not.toContain(file.token)
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

		expect(droppedToolFile(out.types)).toEqual(file)
	})

	it('ignores the type when no drag of ours is going on', () => {
		const forged = transfer()
		forged.setData(TOOL_FILE_TYPE, file.token)

		expect(isToolDrag(forged.types)).toBe(false)
		expect(droppedToolFile(forged.types)).toBeUndefined()
	})

	it('ends with the drag, so a later forged drop finds nothing', () => {
		const out = transfer()
		beginToolDrag(out, file)
		endToolDrag()

		expect(droppedToolFile(out.types)).toBeUndefined()
	})

	it('ends on a dragend or drop that reaches the window, as when the dragged row is gone', () => {
		const target = new EventTarget()
		const stop = watchDragEnd(target)
		for (const kind of ['dragend', 'drop']) {
			const out = transfer()
			beginToolDrag(out, file)
			target.dispatchEvent(new Event(kind))
			expect(droppedToolFile(out.types)).toBeUndefined()
		}
		stop()
		beginToolDrag(transfer(), file)
		target.dispatchEvent(new Event('dragend'))
		expect(isToolDrag([TOOL_FILE_TYPE])).toBe(true)
	})

	it('ends when the source row goes, but only for its own file', () => {
		beginToolDrag(transfer(), file)
		endToolDrag({ token: 'c'.repeat(32), name: 'other.pdf' })
		expect(isToolDrag([TOOL_FILE_TYPE])).toBe(true)
		endToolDrag(file)
		expect(isToolDrag([TOOL_FILE_TYPE])).toBe(false)
	})
})

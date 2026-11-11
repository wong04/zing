import { describe, expect, it } from 'vitest'
import { handedFile } from './handedFile'

describe('a file handed to the viewer', () => {
	it('is the token and name the host sent', () => {
		expect(handedFile('{"token":"abc","name":"report.pdf"}')).toEqual({ token: 'abc', name: 'report.pdf' })
	})

	it('refuses anything else', () => {
		expect(() => handedFile('https://a.test/x.pdf')).toThrow(SyntaxError)
		expect(() => handedFile('null')).toThrow(TypeError)
		expect(() => handedFile('{"token":1,"name":"x.pdf"}')).toThrow(TypeError)
	})
})

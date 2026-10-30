import { describe, expect, it } from 'vitest'
import { extension, fileName, folderName } from './filePath'

describe('file paths', () => {
	it('takes the name and the containing folder from either separator', () => {
		expect(fileName('C:\\Users\\me\\Pictures\\holiday.PNG')).toBe('holiday.PNG')
		expect(folderName('C:\\Users\\me\\Pictures\\holiday.PNG')).toBe('Pictures')
		expect(fileName('/home/me/notes.txt')).toBe('notes.txt')
		expect(folderName('notes.txt')).toBe('')
	})

	it('reads the extension in lowercase', () => {
		expect(extension('C:\\a\\b\\Report.Final.PDF')).toBe('pdf')
		expect(extension('archive.tar.gz')).toBe('gz')
	})

	it('finds no extension where there is none', () => {
		expect(extension('Makefile')).toBe('')
		expect(extension('.gitignore')).toBe('')
		expect(extension('trailing.')).toBe('')
	})
})

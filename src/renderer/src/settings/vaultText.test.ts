import { describe, expect, it } from 'vitest'
import { earlierPasswords, loginsDetail } from './vaultText'

describe('loginsDetail', () => {
	it('counts the logins and says Hello is asked at most once every 5 minutes', () => {
		expect(loginsDetail(1)).toBe('1 login. Showing a password asks for Windows Hello, at most once every 5 minutes.')
		expect(loginsDetail(0)).toBe('0 logins. Showing a password asks for Windows Hello, at most once every 5 minutes.')
		expect(loginsDetail(3)).toBe('3 logins. Showing a password asks for Windows Hello, at most once every 5 minutes.')
	})
})

describe('earlierPasswords', () => {
	it('says one earlier password, or how many', () => {
		expect(earlierPasswords(1)).toBe('1 earlier password')
		expect(earlierPasswords(2)).toBe('2 earlier passwords')
	})
})

// Krackn has no checks for KeychainAutofill; these follow its script's rules and
// `KeychainAutofill.field(from:)`.
import { describe, expect, it } from 'vitest'
import { AutofillMessageError, fieldKind, fieldMessageKey, parseFieldMessage, type InputDescription } from './autofillFields'

function input(overrides: Partial<InputDescription> = {}): InputDescription {
	return {
		tagName: 'INPUT',
		disabled: false,
		readOnly: false,
		type: 'text',
		autocomplete: '',
		name: '',
		id: '',
		ariaLabel: '',
		placeholder: '',
		...overrides,
	}
}

describe('which fields are sign-in fields', () => {
	it('a password field wants the password', () => {
		expect(fieldKind(input({ type: 'password' }))).toBe('password')
		expect(fieldKind(input({ type: 'PASSWORD' }))).toBe('password')
	})
	it('autocomplete username or email, or an email field, wants the username', () => {
		expect(fieldKind(input({ autocomplete: 'username' }))).toBe('username')
		expect(fieldKind(input({ autocomplete: 'section-login username webauthn' }))).toBe('username')
		expect(fieldKind(input({ autocomplete: 'email' }))).toBe('username')
		expect(fieldKind(input({ type: 'email' }))).toBe('username')
	})
	it('a text field named like an account wants the username', () => {
		expect(fieldKind(input({ name: 'login' }))).toBe('username')
		expect(fieldKind(input({ id: 'identifierId' }))).toBe('username')
		expect(fieldKind(input({ ariaLabel: 'Email or phone' }))).toBe('username')
		expect(fieldKind(input({ placeholder: 'E-mail address' }))).toBe('username')
		expect(fieldKind(input({ name: 'signin_name' }))).toBe('username')
		expect(fieldKind(input({ type: 'tel', name: 'account' }))).toBe('username')
	})
	it('an ordinary text field is not', () => {
		expect(fieldKind(input({ name: 'q', placeholder: 'Search' }))).toBeNull()
	})
	it('a hint only counts on text-like fields', () => {
		expect(fieldKind(input({ type: 'search', name: 'username' }))).toBeNull()
		expect(fieldKind(input({ type: 'checkbox', name: 'remember-login' }))).toBeNull()
	})
	it('disabled, read-only and non-input elements never are', () => {
		expect(fieldKind(input({ type: 'password', disabled: true }))).toBeNull()
		expect(fieldKind(input({ type: 'password', readOnly: true }))).toBeNull()
		expect(fieldKind(input({ tagName: 'TEXTAREA', name: 'username' }))).toBeNull()
	})
})

describe('reading the script\'s messages', () => {
	it('a field and where it is', () => {
		expect(parseFieldMessage({ kind: 'password', x: 10, y: 20.5, w: 200, h: 30 })).toEqual({
			kind: 'password',
			x: 10,
			y: 20.5,
			width: 200,
			height: 30,
		})
	})
	it('"none" means no sign-in field has focus', () => {
		expect(parseFieldMessage({ kind: 'none' })).toBeNull()
	})
	it('a field with no size has nowhere to show the key', () => {
		expect(parseFieldMessage({ kind: 'username', x: 0, y: 0, w: 0, h: 30 })).toBeNull()
		expect(parseFieldMessage({ kind: 'username', x: 0, y: 0, w: 120, h: -1 })).toBeNull()
	})
	it('anything else is a bug, not "no field"', () => {
		expect(() => parseFieldMessage(null)).toThrow(AutofillMessageError)
		expect(() => parseFieldMessage('password')).toThrow(AutofillMessageError)
		expect(() => parseFieldMessage({ kind: 'credit-card', x: 0, y: 0, w: 1, h: 1 })).toThrow(AutofillMessageError)
		expect(() => parseFieldMessage({ kind: 'password', x: '1', y: 0, w: 1, h: 1 })).toThrow(AutofillMessageError)
		expect(() => parseFieldMessage({ kind: 'password', x: Number.NaN, y: 0, w: 1, h: 1 })).toThrow(AutofillMessageError)
	})
	it('the change key rounds to whole pixels', () => {
		expect(fieldMessageKey('username', 10.4, 20.6, 199.5, 30)).toBe('username,10,21,200,30')
	})
})

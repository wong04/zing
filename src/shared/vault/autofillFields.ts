// The pure side of sign-in field detection, ported from the detection script in Krackn's
// `KeychainAutofill` (Features/Browser/KeychainAutofill.swift). `autofillScript.ts` runs
// the same rules in the page; its tests check the two agree.

export type CredentialFieldKind = 'username' | 'password'

/** What the page script reads off an element to classify it. */
export interface InputDescription {
	tagName: string
	disabled: boolean
	readOnly: boolean
	/** `el.type`; a real `<input>` with no type attribute reports "text". */
	type: string
	/** `el.autocomplete`. */
	autocomplete: string
	name: string
	id: string
	ariaLabel: string
	placeholder: string
}

/** Words in a text field's name, id, label or placeholder that mean "who you are". */
export const usernameHint = /user|login|e-?mail|account|identifier|sign.?in/

const hintableTypes: ReadonlySet<string> = new Set(['text', '', 'tel'])

/** Which half of a credential a field wants, or null when it isn't a sign-in field. */
export function fieldKind(input: InputDescription): CredentialFieldKind | null {
	if (input.tagName !== 'INPUT' || input.disabled || input.readOnly) return null
	const type = input.type.toLowerCase()
	const autocomplete = input.autocomplete.toLowerCase()
	if (type === 'password') return 'password'
	if (autocomplete.includes('username') || autocomplete === 'email' || type === 'email') return 'username'
	const hint = [input.name, input.id, input.ariaLabel, input.placeholder].join(' ').toLowerCase()
	if (hintableTypes.has(type) && usernameHint.test(hint)) return 'username'
	return null
}

/** The focused sign-in field, in the page's CSS pixels relative to the viewport. */
export interface CredentialField {
	kind: CredentialFieldKind
	x: number
	y: number
	width: number
	height: number
}

export class AutofillMessageError extends Error {
	override name = 'AutofillMessageError'
}

function isFiniteNumber(value: unknown): value is number {
	return typeof value === 'number' && Number.isFinite(value)
}

/**
 * Reads a message from the detection script (`{ kind, x, y, w, h }`, or
 * `{ kind: 'none' }`). Null when no sign-in field has focus, including one
 * with no size, which there is nothing to show a key over. Anything else
 * means the script and the host disagree, which is a bug, so it throws.
 */
export function parseFieldMessage(body: unknown): CredentialField | null {
	if (typeof body !== 'object' || body === null) throw new AutofillMessageError('autofill message is not an object')
	const { kind, x, y, w, h } = body as Record<string, unknown>
	if (kind === 'none') return null
	if (kind !== 'username' && kind !== 'password') {
		throw new AutofillMessageError(`unknown autofill field kind: ${JSON.stringify(kind)}`)
	}
	if (!isFiniteNumber(x) || !isFiniteNumber(y) || !isFiniteNumber(w) || !isFiniteNumber(h)) {
		throw new AutofillMessageError('autofill message has no usable position')
	}
	if (w <= 0 || h <= 0) return null
	return { kind, x, y, width: w, height: h }
}

/**
 * The script reports a field only when this changes, so holding still on a
 * field doesn't send a message on every poll.
 */
export function fieldMessageKey(kind: CredentialFieldKind, x: number, y: number, w: number, h: number): string {
	return [kind, ...[x, y, w, h].map(Math.round)].join(',')
}

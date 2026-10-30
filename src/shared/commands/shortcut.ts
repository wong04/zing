/**
 * A key combination, keyed by the physical key (`KeyboardEvent.code`) rather
 * than the produced character: Ctrl+Shift+1 produces "!" as its key, and
 * Ctrl+= is the "Plus" people actually press.
 */
export interface Shortcut {
	readonly code: KeyCode
	readonly ctrl?: true
	readonly shift?: true
	readonly alt?: true
}

/** The fields of an Electron `Input` / DOM `KeyboardEvent` that matching needs. */
export interface KeyInput {
	readonly code: string
	readonly control: boolean
	readonly shift: boolean
	readonly alt: boolean
	readonly meta: boolean
}

const LETTERS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
const DIGITS = '0123456789'

type Letter = 'A' | 'B' | 'C' | 'D' | 'E' | 'F' | 'G' | 'H' | 'I' | 'J' | 'K' | 'L' | 'M'
	| 'N' | 'O' | 'P' | 'Q' | 'R' | 'S' | 'T' | 'U' | 'V' | 'W' | 'X' | 'Y' | 'Z'
type Digit = '0' | '1' | '2' | '3' | '4' | '5' | '6' | '7' | '8' | '9'
type FunctionKey = 'F1' | 'F2' | 'F3' | 'F4' | 'F5' | 'F6' | 'F7' | 'F8' | 'F9' | 'F10' | 'F11' | 'F12'

const NAMED_KEYS = {
	Equal: { accelerator: '=', label: '=' },
	Minus: { accelerator: '-', label: '-' },
	Comma: { accelerator: ',', label: ',' },
	Period: { accelerator: '.', label: '.' },
	Backslash: { accelerator: '\\', label: '\\' },
	NumpadAdd: { accelerator: 'numadd', label: 'Num +' },
	NumpadSubtract: { accelerator: 'numsub', label: 'Num -' },
	Numpad0: { accelerator: 'num0', label: 'Num 0' },
	Tab: { accelerator: 'Tab', label: 'Tab' },
	Escape: { accelerator: 'Esc', label: 'Esc' },
	Home: { accelerator: 'Home', label: 'Home' },
	PageUp: { accelerator: 'PageUp', label: 'PgUp' },
	PageDown: { accelerator: 'PageDown', label: 'PgDn' },
	ArrowLeft: { accelerator: 'Left', label: 'Left' },
	ArrowRight: { accelerator: 'Right', label: 'Right' },
} as const

export type KeyCode = `Key${Letter}` | `Digit${Digit}` | FunctionKey | keyof typeof NAMED_KEYS

function keyName(code: KeyCode, field: 'accelerator' | 'label'): string {
	if (code.startsWith('Key') && LETTERS.includes(code.slice(3))) return code.slice(3)
	if (code.startsWith('Digit') && DIGITS.includes(code.slice(5))) return code.slice(5)
	if (code in NAMED_KEYS) return NAMED_KEYS[code as keyof typeof NAMED_KEYS][field]
	// Function keys are named the same everywhere.
	return code
}

function modifiers(shortcut: Shortcut): string[] {
	return [shortcut.ctrl && 'Ctrl', shortcut.alt && 'Alt', shortcut.shift && 'Shift'].filter(
		(m): m is string => typeof m === 'string',
	)
}

/** Electron accelerator string, for showing the shortcut in a menu. */
export function toAccelerator(shortcut: Shortcut): string {
	return [...modifiers(shortcut), keyName(shortcut.code, 'accelerator')].join('+')
}

/** Human-readable form for tooltips, e.g. "Ctrl+Shift+T". */
export function shortcutLabel(shortcut: Shortcut): string {
	return [...modifiers(shortcut), keyName(shortcut.code, 'label')].join('+')
}

export function shortcutMatches(shortcut: Shortcut, input: KeyInput): boolean {
	return (
		shortcut.code === input.code &&
		Boolean(shortcut.ctrl) === input.control &&
		Boolean(shortcut.shift) === input.shift &&
		Boolean(shortcut.alt) === input.alt &&
		!input.meta
	)
}

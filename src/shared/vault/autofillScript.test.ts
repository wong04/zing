// There is no DOM in the test environment, so the script runs against a small fake one.
// What matters is that its decisions match `fieldKind` and its messages `parseFieldMessage`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { autofillBridgeName, detectionScript, fillScript } from './autofillScript'
import { fieldKind, fieldMessageKey, parseFieldMessage, type InputDescription } from './autofillFields'

type Listener = () => void

class FakeEl {
	tagName = 'INPUT'
	disabled = false
	readOnly = false
	type = 'text'
	autocomplete = ''
	name = ''
	id = ''
	placeholder = ''
	aria = ''
	isConnected = true
	value = ''
	shadowRoot: { activeElement: FakeEl | null } | null = null
	contentDocument: FakeDoc | null = null
	clientLeft = 0
	clientTop = 0
	rect = { left: 0, top: 0, width: 100, height: 20 }
	events: string[] = []
	ownerDocument: { defaultView: unknown } = { defaultView: null }
	constructor(init: Partial<FakeEl> = {}) {
		Object.assign(this, init)
	}
	getAttribute(name: string): string | null {
		return name === 'aria-label' ? this.aria : null
	}
	getBoundingClientRect() {
		return this.rect
	}
	focus(): void {}
	dispatchEvent(event: { type: string }): boolean {
		this.events.push(event.type)
		return true
	}
	describe(): InputDescription {
		return {
			tagName: this.tagName,
			disabled: this.disabled,
			readOnly: this.readOnly,
			type: this.type,
			autocomplete: this.autocomplete,
			name: this.name,
			id: this.id,
			ariaLabel: this.aria,
			placeholder: this.placeholder,
		}
	}
}

class FakeDoc {
	activeElement: FakeEl | null = null
	frames: FakeEl[] = []
	listeners = new Map<string, Listener[]>()
	focused = true
	addEventListener(type: string, fn: Listener): void {
		this.listeners.set(type, [...(this.listeners.get(type) ?? []), fn])
	}
	querySelectorAll(): FakeEl[] {
		return this.frames
	}
	hasFocus(): boolean {
		return this.focused
	}
}

class FakeEvent {
	constructor(public type: string) {}
}

const setterCalls: Array<[FakeEl, string]> = []

// The page's real `HTMLInputElement.prototype.value` setter: the script must go through it.
class FakeInput {}
Object.defineProperty(FakeInput.prototype, 'value', {
	set(this: FakeEl, v: string) {
		setterCalls.push([this, v])
	},
	configurable: true,
})

let doc: FakeDoc
let messages: unknown[]
let windowListeners: Map<string, Listener[]>
let win: Record<string, unknown>

function makeWindow(): Record<string, unknown> {
	return {
		[autofillBridgeName]: (m: unknown) => messages.push(m),
		HTMLInputElement: FakeInput,
		addEventListener: (type: string, fn: Listener) => windowListeners.set(type, [...(windowListeners.get(type) ?? []), fn]),
	}
}

function run(target: Record<string, unknown> = win): void {
	new Function('window', 'document', 'WeakSet', 'Event', detectionScript)(target, doc, WeakSet, FakeEvent)
}

function install(): void {
	doc = new FakeDoc()
	messages = []
	windowListeners = new Map()
	setterCalls.length = 0
	win = makeWindow()
	run()
}

function focus(el: FakeEl): void {
	doc.activeElement = el
	for (const fn of doc.listeners.get('focusin') ?? []) fn()
	vi.advanceTimersByTime(0)
}

function evalFill(value: string, kind: 'username' | 'password'): unknown {
	return new Function('window', `return ${fillScript(value, kind)}`)(win)
}

beforeEach(() => {
	vi.useFakeTimers()
})
afterEach(() => {
	vi.useRealTimers()
})

const sample: Array<Partial<FakeEl>> = [
	{ type: 'password' },
	{ type: 'PASSWORD' },
	{ autocomplete: 'section-login username webauthn' },
	{ autocomplete: 'email' },
	{ type: 'email' },
	{ name: 'login' },
	{ id: 'identifierId' },
	{ aria: 'Email or phone' },
	{ placeholder: 'E-mail address' },
	{ type: 'tel', name: 'account' },
	{ name: 'q', placeholder: 'Search' },
	{ type: 'search', name: 'username' },
	{ type: 'checkbox', name: 'remember-login' },
	{ type: 'password', disabled: true },
	{ type: 'password', readOnly: true },
	{ tagName: 'TEXTAREA', name: 'username' },
]

describe('the script classifies fields as fieldKind does', () => {
	for (const init of sample) {
		it(JSON.stringify(init), () => {
			install()
			const el = new FakeEl(init)
			focus(el)
			const expected = fieldKind(el.describe())
			if (expected === null) {
				expect(messages).toEqual([])
				return
			}
			expect(messages).toHaveLength(1)
			expect(parseFieldMessage(messages[0])?.kind).toBe(expected)
		})
	}
})

describe('detection', () => {
	it('reports a field that was already focused when the script ran', () => {
		doc = new FakeDoc()
		messages = []
		windowListeners = new Map()
		win = makeWindow()
		doc.activeElement = new FakeEl({ type: 'password' })
		run()
		expect(messages).toEqual([{ kind: 'password', x: 0, y: 0, w: 100, h: 20 }])
	})
	it('the message is what parseFieldMessage reads', () => {
		install()
		focus(new FakeEl({ type: 'email', rect: { left: 10, top: 20.5, width: 200, height: 30 } }))
		expect(parseFieldMessage(messages[0])).toEqual({ kind: 'username', x: 10, y: 20.5, width: 200, height: 30 })
	})
	it('reports only when the field moves or changes, with fieldMessageKey rounding', () => {
		install()
		const el = new FakeEl({ type: 'password' })
		focus(el)
		focus(el)
		el.rect = { left: 0.2, top: 0.3, width: 100.1, height: 20 }
		focus(el)
		expect(messages).toHaveLength(1)
		el.rect = { left: 5, top: 0, width: 100, height: 20 }
		focus(el)
		expect(messages).toHaveLength(2)
		expect(fieldMessageKey('password', 5, 0, 100, 20)).toBe('password,5,0,100,20')
	})
	it('says "none" once when focus leaves a sign-in field', () => {
		install()
		focus(new FakeEl({ type: 'password' }))
		focus(new FakeEl({ name: 'q' }))
		focus(new FakeEl({ name: 'q' }))
		expect(messages[1]).toEqual({ kind: 'none' })
		expect(messages).toHaveLength(2)
	})
	it('looks through shadow roots', () => {
		install()
		const inner = new FakeEl({ type: 'password' })
		focus(new FakeEl({ tagName: 'MY-FIELD', shadowRoot: { activeElement: inner } }))
		expect(parseFieldMessage(messages[0])?.kind).toBe('password')
	})
	it('looks into a same-origin frame and adds its offset', () => {
		install()
		const innerDoc = new FakeDoc()
		innerDoc.activeElement = new FakeEl({ type: 'password', rect: { left: 5, top: 6, width: 100, height: 20 } })
		const frame = new FakeEl({
			tagName: 'IFRAME',
			contentDocument: innerDoc,
			rect: { left: 100, top: 200, width: 400, height: 300 },
			clientLeft: 1,
			clientTop: 2,
		})
		focus(frame)
		expect(parseFieldMessage(messages[0])).toMatchObject({ x: 106, y: 208 })
	})
	it('never touches a cross-origin frame', () => {
		install()
		focus(new FakeEl({ tagName: 'IFRAME', contentDocument: null }))
		expect(messages).toEqual([])
		const throwing = new FakeEl({ tagName: 'IFRAME' })
		Object.defineProperty(throwing, 'contentDocument', {
			get() {
				throw new Error('SecurityError')
			},
		})
		focus(throwing)
		expect(messages).toEqual([])
	})
	it('listens inside same-origin frames and skips the others', () => {
		install()
		const innerDoc = new FakeDoc()
		doc.frames = [new FakeEl({ tagName: 'IFRAME', contentDocument: innerDoc }), new FakeEl({ tagName: 'IFRAME' })]
		for (const fn of windowListeners.get('load') ?? []) fn()
		expect([...innerDoc.listeners.keys()].sort()).toEqual(['focusin', 'focusout', 'keyup', 'pointerdown'])
	})
	it('re-checks on load, click, key and focus', () => {
		install()
		expect([...doc.listeners.keys()].sort()).toEqual(['focusin', 'focusout', 'keyup', 'load', 'pointerdown'])
		expect(windowListeners.has('focus')).toBe(true)
		expect(windowListeners.has('load')).toBe(true)
	})
	it('polls lightly, only while the page has focus', () => {
		install()
		doc.activeElement = new FakeEl({ type: 'password' })
		doc.focused = false
		vi.advanceTimersByTime(1400)
		expect(messages).toEqual([])
		doc.focused = true
		vi.advanceTimersByTime(700)
		expect(messages).toHaveLength(1)
	})
	it('installs once however often it is injected', () => {
		install()
		const before = doc.listeners.get('focusin')?.length
		run()
		expect(doc.listeners.get('focusin')?.length).toBe(before)
	})
	it('fails loudly when the bridge is missing', () => {
		install()
		expect(() => run({ addEventListener: () => {} })).toThrow(/bridge is missing/)
	})
})

describe('filling', () => {
	it('sets through the native setter then fires input and change', () => {
		install()
		const el = new FakeEl({ type: 'password', ownerDocument: { defaultView: win } })
		focus(el)
		expect(evalFill('hunter2', 'password')).toBe(true)
		expect(setterCalls).toEqual([[el, 'hunter2']])
		expect(el.events).toEqual(['input', 'change'])
	})
	it('refuses the wrong half, a detached field, or no field', () => {
		install()
		const el = new FakeEl({ type: 'password', ownerDocument: { defaultView: win } })
		expect(evalFill('x', 'password')).toBe(false)
		focus(el)
		expect(evalFill('x', 'username')).toBe(false)
		el.isConnected = false
		expect(evalFill('x', 'password')).toBe(false)
		expect(setterCalls).toEqual([])
	})
	it('a value with quotes, script tags or line separators stays a string literal', () => {
		install()
		const el = new FakeEl({ type: 'password', ownerDocument: { defaultView: win } })
		focus(el)
		const value = '"\'</script>\n '
		expect(evalFill(value, 'password')).toBe(true)
		expect(setterCalls[0]?.[1]).toBe(value)
	})
})

// There is no DOM in the test environment, so the script runs against a small fake one.
// What matters is that its decisions match `fieldKind` and its messages `parseFieldMessage`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { autofillBridgeName, autofillPickerEnterFlag, detectionScript, fillScript } from './autofillScript'
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
	ownerDocument: { defaultView: unknown; querySelectorAll?: () => FakeEl[] } = { defaultView: null }
	form: { querySelectorAll: () => FakeEl[] } | null = null
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
	location = { origin: 'https://example.com' }
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
	const w: Record<string, unknown> = {
		// Every message goes out under the script's key; the test unwraps it.
		chrome: { webview: { postMessage: (m: Record<string, unknown>) => messages.push(m[autofillBridgeName]) } },
		HTMLInputElement: FakeInput,
		location: { origin: 'https://example.com' },
		addEventListener: (type: string, fn: Listener) => windowListeners.set(type, [...(windowListeners.get(type) ?? []), fn]),
		getComputedStyle: () => ({ display: 'block', visibility: 'visible' }),
	}
	w['top'] = w
	return w
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

function evalFill(username: string, password: string, origin = 'https://example.com'): unknown {
	return new Function('window', 'location', `return ${fillScript(origin, username, password)}`)(win, win['location'])
}

/** A username box and a password box in one form, in that order. */
function signInForm(): { user: FakeEl; pass: FakeEl } {
	const inputs: FakeEl[] = []
	const form = { querySelectorAll: () => inputs }
	const user = new FakeEl({ type: 'email', form, ownerDocument: { defaultView: win } })
	const pass = new FakeEl({ type: 'password', form, ownerDocument: { defaultView: win } })
	inputs.push(new FakeEl({ type: 'checkbox', name: 'remember', form }), user, pass)
	return { user, pass }
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
		expect([...innerDoc.listeners.keys()].sort()).toEqual(['click', 'focusin', 'focusout', 'keydown', 'keyup', 'pointerdown', 'submit'])
	})
	it('re-checks on load, click, key and focus', () => {
		install()
		expect([...doc.listeners.keys()].sort()).toEqual(['click', 'focusin', 'focusout', 'keydown', 'keyup', 'load', 'pointerdown', 'submit'])
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
	it('fails loudly when chrome.webview is missing', () => {
		install()
		const bare: Record<string, unknown> = { addEventListener: () => {} }
		bare['top'] = bare
		expect(() => run(bare)).toThrow(/chrome.webview is missing/)
	})
	it('does nothing in a frame: the top frame reaches same-origin frames itself', () => {
		install()
		const framed = makeWindow()
		framed['top'] = win
		doc = new FakeDoc()
		doc.activeElement = new FakeEl({ type: 'password' })
		run(framed)
		expect(messages).toEqual([])
		expect(doc.listeners.size).toBe(0)
	})
	it('never reports keys: the host reads real key presses itself', () => {
		install()
		focus(new FakeEl({ type: 'password' }))
		// It listens for Enter in a password box to see a submit, but never posts a key.
		const other = new FakeEl({ type: 'text' })
		for (const fn of doc.listeners.get('keydown') ?? []) (fn as (e: unknown) => void)({ key: 'ArrowDown', target: other })
		vi.advanceTimersByTime(0)
		expect(detectionScript).not.toContain("kind: 'key'")
		expect(messages).toHaveLength(1)
	})
	it('skips a frame of another origin even when its document is readable', () => {
		install()
		const innerDoc = new FakeDoc()
		innerDoc.location = { origin: 'https://other.example' }
		innerDoc.activeElement = new FakeEl({ type: 'password' })
		const frame = new FakeEl({ tagName: 'IFRAME', contentDocument: innerDoc })
		doc.frames = [frame]
		for (const fn of windowListeners.get('load') ?? []) fn()
		expect(innerDoc.listeners.size).toBe(0)
		focus(frame)
		expect(messages).toEqual([])
	})
})

describe('filling', () => {
	it('fills the focused username box and the password box after it, through the native setter', () => {
		install()
		const { user, pass } = signInForm()
		focus(user)
		expect(evalFill('ada', 'hunter2')).toBe(true)
		expect(setterCalls).toEqual([
			[pass, 'hunter2'],
			[user, 'ada'],
		])
		expect(user.events).toEqual(['input', 'change'])
		expect(pass.events).toEqual(['input', 'change'])
	})
	it('from the password box, fills the username box before it', () => {
		install()
		const { user, pass } = signInForm()
		focus(pass)
		expect(evalFill('ada', 'hunter2')).toBe(true)
		expect(setterCalls).toEqual([
			[user, 'ada'],
			[pass, 'hunter2'],
		])
	})
	it('fills just the field when it has no other half', () => {
		install()
		const el = new FakeEl({ type: 'password', ownerDocument: { defaultView: win, querySelectorAll: () => [] } })
		focus(el)
		expect(evalFill('ada', 'hunter2')).toBe(true)
		expect(setterCalls).toEqual([[el, 'hunter2']])
	})
	it('refuses when the page is no longer on the origin the host checked', () => {
		install()
		const { user } = signInForm()
		focus(user)
		expect(evalFill('ada', 'hunter2', 'https://evil.example')).toBe(false)
		expect(setterCalls).toEqual([])
	})
	it('refuses a detached field, or no field', () => {
		install()
		const { user } = signInForm()
		expect(evalFill('a', 'x')).toBe(false)
		focus(user)
		user.isConnected = false
		expect(evalFill('a', 'x')).toBe(false)
		expect(setterCalls).toEqual([])
	})
	it('values with quotes, script tags or line separators stay string literals', () => {
		install()
		const { user } = signInForm()
		focus(user)
		const value = '"\'</script>\n  '
		expect(evalFill(value, value)).toBe(true)
		expect(setterCalls.map(([, v]) => v)).toEqual([value, value])
	})
})

/** A sign-in form with the user's typed values, as a submit event's target. */
function filledForm(username: string, password: string): { form: { tagName: string; querySelectorAll: () => FakeEl[] }; inputs: FakeEl[] } {
	const inputs: FakeEl[] = []
	const form = { tagName: 'FORM', querySelectorAll: () => inputs }
	const owner = { defaultView: win }
	inputs.push(new FakeEl({ type: 'email', value: username, form, ownerDocument: owner }), new FakeEl({ type: 'password', value: password, form, ownerDocument: owner }))
	return { form, inputs }
}

/** A form of the given inputs, each owned by the window, as a submit event's target. */
function formOf(...init: Array<Partial<FakeEl>>): { form: { tagName: string; querySelectorAll: () => FakeEl[] }; inputs: FakeEl[] } {
	const inputs: FakeEl[] = []
	const form = { tagName: 'FORM', querySelectorAll: () => inputs }
	const owner = { defaultView: win }
	for (const i of init) inputs.push(new FakeEl({ ...i, form, ownerDocument: owner }))
	return { form, inputs }
}

/** The first page of a two-step sign-in: a username box and no password box. */
const usernameForm = (username: string) => formOf({ type: 'email', value: username })

const hide = (hidden: FakeEl): void => {
	win['getComputedStyle'] = (el: FakeEl) => ({ display: el === hidden ? 'none' : 'block', visibility: 'visible' })
}

const submit = (target: unknown): void => {
	for (const fn of doc.listeners.get('submit') ?? []) (fn as (e: unknown) => void)({ target })
}

const kindsOf = (kind: string): unknown[] => messages.filter((m) => (m as { kind: string }).kind === kind)

describe('sign-in reports', () => {
	it('a submitted form reports its username and password', () => {
		install()
		submit(filledForm('ada@example.test', 'hunter2').form)
		expect(messages).toEqual([{ kind: 'submit', username: 'ada@example.test', password: 'hunter2' }])
	})
	it('the same submit seen again within a second (click, Enter, submit) is reported once', () => {
		install()
		const { form } = filledForm('ada', 'hunter2')
		submit(form)
		submit(form)
		vi.advanceTimersByTime(1100)
		submit(form)
		expect(kindsOf('submit')).toHaveLength(2)
	})
	it('a sign-up form that repeats the password reports it; a change-password form does not', () => {
		install()
		const repeated = filledForm('ada', 'hunter2')
		repeated.inputs.push(new FakeEl({ type: 'password', value: 'hunter2', form: repeated.form, ownerDocument: { defaultView: win } }))
		submit(repeated.form)
		expect(messages).toHaveLength(1)

		install()
		const changing = filledForm('ada', 'old')
		changing.inputs.push(new FakeEl({ type: 'password', value: 'new', form: changing.form, ownerDocument: { defaultView: win } }))
		submit(changing.form)
		expect(messages).toEqual([])
	})
	it('reports nothing without a password, or past the limits; an empty username is sent empty', () => {
		install()
		submit(filledForm('ada', '').form)
		submit(filledForm('ada', 'p'.repeat(1025)).form)
		submit(filledForm('u'.repeat(513), 'hunter2').form)
		expect(messages).toEqual([])
		submit(filledForm('u'.repeat(512), 'p'.repeat(1024)).form)
		expect(messages).toHaveLength(1)
		submit(filledForm('', 'hunter2').form)
		expect(messages[1]).toEqual({ kind: 'submit', username: '', password: 'hunter2' })
	})
	it('a hidden password box is not the form\'s, so the form is a username step; anything not a form is ignored', () => {
		install()
		const { form, inputs } = filledForm('ada', 'hunter2')
		hide(inputs[1]!)
		submit(form)
		submit({ tagName: 'DIV' })
		expect(messages).toEqual([{ kind: 'username-step', username: 'ada' }])
	})
	it('a username-only form reports a username step, and no landing after it', () => {
		install()
		submit(usernameForm('ada@example.test').form)
		vi.advanceTimersByTime(5000)
		expect(messages).toEqual([{ kind: 'username-step', username: 'ada@example.test' }])
	})
	it('a username step is reported once per second however many events fire', () => {
		install()
		const { form } = usernameForm('ada')
		submit(form)
		submit(form)
		vi.advanceTimersByTime(1100)
		submit(form)
		expect(kindsOf('username-step')).toHaveLength(2)
	})
	it('an empty or over-long username step reports nothing', () => {
		install()
		submit(usernameForm('').form)
		submit(usernameForm('u'.repeat(513)).form)
		expect(messages).toEqual([])
		submit(usernameForm('u'.repeat(512)).form)
		expect(kindsOf('username-step')).toHaveLength(1)
	})
	it('a password-only form reports a submit with an empty username', () => {
		install()
		submit(formOf({ type: 'password', value: 'hunter2' }).form)
		expect(messages).toEqual([{ kind: 'submit', username: '', password: 'hunter2' }])
	})
	it('a password form sends a hidden username or email field separately; another hidden text field is not one', () => {
		install()
		const marked = formOf({ type: 'password', value: 'hunter2' }, { type: 'email', value: 'ada@example.test' })
		hide(marked.inputs[1]!)
		submit(marked.form)
		expect(messages).toEqual([{ kind: 'submit', username: '', password: 'hunter2', hiddenUsername: 'ada@example.test' }])

		install()
		const other = formOf({ type: 'text', name: 'user_id', value: '12345' }, { type: 'password', value: 'hunter2' })
		hide(other.inputs[0]!)
		submit(other.form)
		expect(messages).toEqual([{ kind: 'submit', username: '', password: 'hunter2' }])
	})
	it('a box that looks like a one-time code is not a password', () => {
		const codes: Array<Partial<FakeEl>> = [
			{ autocomplete: 'one-time-code' },
			{ name: 'otp' },
			{ id: 'verificationCode' },
			{ name: 'pin' },
			{ aria: '2FA token' },
			{ name: 'mfa' },
		]
		for (const code of codes) {
			install()
			submit(formOf({ type: 'password', value: '123456', ...code }).form)
			expect(messages).toEqual([])
		}
	})
	it('a sign-in form with a visible username still reports a box labelled PIN or passcode', () => {
		install()
		submit(formOf({ type: 'email', value: 'ada' }, { type: 'password', value: '1234', name: 'pin' }).form)
		expect(messages).toEqual([{ kind: 'submit', username: 'ada', password: '1234' }])
	})
	it('Enter in a username box with no password box on the page reports that box\'s username step', () => {
		install()
		const owner = Object.assign(doc, { defaultView: win })
		const user = new FakeEl({ type: 'email', value: 'ada', ownerDocument: owner })
		const other = new FakeEl({ type: 'email', value: 'newsletter@example.test', ownerDocument: owner })
		doc.frames = [user, other]
		for (const fn of doc.listeners.get('keydown') ?? []) (fn as (e: unknown) => void)({ key: 'Enter', target: user })
		expect(messages).toEqual([{ kind: 'username-step', username: 'ada' }])
	})
	it('Enter in a form-less password box reports a submit; Enter in a text box does not', () => {
		install()
		const { inputs } = filledForm('ada', 'hunter2')
		const [user, pass] = inputs as [FakeEl, FakeEl]
		const owner = Object.assign(doc, { defaultView: win })
		doc.frames = inputs
		user.form = null
		pass.form = null
		user.ownerDocument = owner
		pass.ownerDocument = owner
		const press = (target: FakeEl): void => {
			for (const fn of doc.listeners.get('keydown') ?? []) (fn as (e: unknown) => void)({ key: 'Enter', target })
		}
		press(user)
		expect(messages).toEqual([])
		press(pass)
		expect(messages).toEqual([{ kind: 'submit', username: 'ada', password: 'hunter2' }])
	})
	it('reports a landing for each new document, with whether a password box is on it', () => {
		install()
		for (const fn of windowListeners.get('load') ?? []) fn()
		vi.advanceTimersByTime(300)
		expect(messages).toEqual([{ kind: 'landed', password: false }])

		install()
		doc.frames = [new FakeEl({ type: 'password', ownerDocument: { defaultView: win } })]
		for (const fn of windowListeners.get('load') ?? []) fn()
		vi.advanceTimersByTime(300)
		expect(messages).toEqual([{ kind: 'landed', password: true }])
	})
	it('a document submitted before its own load report is the page being left, so it reports no landing', () => {
		install()
		const { form, inputs } = filledForm('ada', 'hunter2')
		doc.frames = inputs
		for (const fn of windowListeners.get('load') ?? []) fn()
		submit(form)
		vi.advanceTimersByTime(300)
		expect(kindsOf('landed')).toEqual([])
		expect(kindsOf('submit')).toHaveLength(1)
	})
	it('a single-page sign-in that swaps the form away reports one landing, once the box is gone for five checks (about 2 s)', () => {
		install()
		const { form, inputs } = filledForm('ada', 'hunter2')
		doc.frames = inputs
		submit(form)
		vi.advanceTimersByTime(1000)
		expect(messages).toHaveLength(1)
		inputs.forEach((i) => (i.isConnected = false))
		doc.frames = []
		vi.advanceTimersByTime(1600)
		expect(kindsOf('landed')).toHaveLength(0)
		vi.advanceTimersByTime(400)
		expect(kindsOf('landed')).toEqual([{ kind: 'landed', password: false }])
		vi.advanceTimersByTime(5000)
		expect(kindsOf('landed')).toHaveLength(1)
	})
	it('a form that is hidden, disabled or covered while the sign-in is checked is still the form: no landing', () => {
		install()
		const { form, inputs } = filledForm('ada', 'wrong')
		doc.frames = inputs
		submit(form)
		win['getComputedStyle'] = () => ({ display: 'none', visibility: 'hidden' })
		inputs[1]!.disabled = true
		vi.advanceTimersByTime(9000)
		expect(kindsOf('landed')).toEqual([])
	})
	it('a password box that comes back after the form went is reported, once, so a prompt can be withdrawn', () => {
		install()
		const { form, inputs } = filledForm('ada', 'wrong')
		doc.frames = inputs
		submit(form)
		inputs.forEach((i) => (i.isConnected = false))
		doc.frames = []
		vi.advanceTimersByTime(2000)
		expect(kindsOf('landed')).toEqual([{ kind: 'landed', password: false }])
		doc.frames = [new FakeEl({ type: 'password', ownerDocument: { defaultView: win } })]
		vi.advanceTimersByTime(6000)
		expect(kindsOf('landed')).toEqual([
			{ kind: 'landed', password: false },
			{ kind: 'landed', password: true },
		])
	})
	it('a page that keeps its form after a submit reports no landing, and the watch ends after ten seconds', () => {
		install()
		const { form, inputs } = filledForm('ada', 'wrong')
		doc.frames = inputs
		submit(form)
		vi.advanceTimersByTime(12_000)
		expect(messages).toHaveLength(1)
		doc.frames = []
		vi.advanceTimersByTime(5000)
		expect(messages).toHaveLength(1)
	})
	it('a newer submit takes over the watch', () => {
		install()
		const { form, inputs } = filledForm('ada', 'hunter2')
		doc.frames = inputs
		submit(form)
		vi.advanceTimersByTime(1500)
		inputs[1]!.value = 'hunter3'
		submit(form)
		inputs.forEach((i) => (i.isConnected = false))
		doc.frames = []
		vi.advanceTimersByTime(2400)
		expect(kindsOf('landed')).toHaveLength(1)
	})
	it('a spinner that unmounts the form and brings it back inside the settle time reports nothing at all', () => {
		install()
		const { form, inputs } = filledForm('ada', 'wrong')
		doc.frames = inputs
		submit(form)
		inputs.forEach((i) => (i.isConnected = false))
		doc.frames = []
		vi.advanceTimersByTime(1600)
		const again = filledForm('ada', 'wrong')
		doc.frames = again.inputs
		vi.advanceTimersByTime(20_000)
		expect(kindsOf('landed')).toEqual([])
	})
	it('keeps watching for a returning password box for as long as a prompt can stay open, not only the ten-second hold', () => {
		install()
		const { form, inputs } = filledForm('ada', 'wrong')
		doc.frames = inputs
		submit(form)
		inputs.forEach((i) => (i.isConnected = false))
		doc.frames = []
		vi.advanceTimersByTime(2400)
		expect(kindsOf('landed')).toEqual([{ kind: 'landed', password: false }])
		vi.advanceTimersByTime(60_000)
		doc.frames = [new FakeEl({ type: 'password', ownerDocument: { defaultView: win } })]
		vi.advanceTimersByTime(1000)
		expect(kindsOf('landed')).toHaveLength(2)
	})
})

// Ruling 46: plain Enter never reaches the host's AcceleratorKeyPressed, so the script takes it for the picker.
describe('Enter for the account picker', () => {
	class KeyEvent {
		prevented = false
		stopped = false
		isComposing = false
		ctrlKey = false
		altKey = false
		metaKey = false
		constructor(
			public key: string,
			init: Partial<KeyEvent> = {},
		) {
			Object.assign(this, init)
		}
		preventDefault(): void {
			this.prevented = true
		}
		stopImmediatePropagation(): void {
			this.stopped = true
		}
	}
	const press = (key: string, init: Partial<KeyEvent> = {}): KeyEvent => {
		const event = new KeyEvent(key, init)
		for (const fn of windowListeners.get('keydown') ?? []) (fn as unknown as (e: KeyEvent) => void)(event)
		return event
	}
	const pickerEnters = (): unknown[] => messages.filter((m) => (m as { kind?: string }).kind === 'picker-enter')

	it('leaves Enter to the page until the host says an entry is highlighted', () => {
		install()
		const event = press('Enter')
		expect(event.prevented).toBe(false)
		expect(event.stopped).toBe(false)
		expect(pickerEnters()).toEqual([])
	})
	it('takes Enter from the page while the host says so, before any page listener, and posts picker-enter', () => {
		install()
		win[autofillPickerEnterFlag] = true
		const event = press('Enter')
		expect(event.prevented).toBe(true)
		expect(event.stopped).toBe(true)
		expect(pickerEnters()).toEqual([{ kind: 'picker-enter' }])
		win[autofillPickerEnterFlag] = false
		expect(press('Enter').prevented).toBe(false)
	})
	it('takes only plain Enter: other keys, Ctrl or Alt with it, and an IME composition stay the page\'s', () => {
		install()
		win[autofillPickerEnterFlag] = true
		for (const event of [press('a'), press('ArrowDown'), press('Enter', { ctrlKey: true }), press('Enter', { altKey: true }), press('Enter', { isComposing: true })]) expect(event.prevented).toBe(false)
		expect(pickerEnters()).toEqual([])
	})
	it('in a same-origin frame, follows the top document and posts through it', () => {
		install()
		windowListeners = new Map()
		const framed = makeWindow()
		framed['top'] = win
		run(framed)
		win[autofillPickerEnterFlag] = true
		expect(press('Enter').prevented).toBe(true)
		expect(pickerEnters()).toEqual([{ kind: 'picker-enter' }])
	})
	it('in a cross-origin frame, which cannot read the top document, never takes Enter', () => {
		install()
		windowListeners = new Map()
		const framed = makeWindow()
		// Another site's window: it can be compared, but reading anything on it throws.
		framed['top'] = new Proxy(
			{},
			{
				get: () => {
					throw new Error('cross-origin')
				},
			},
		)
		run(framed)
		expect(press('Enter').prevented).toBe(false)
		expect(pickerEnters()).toEqual([])
	})
	it('installs its listener once however often the script runs', () => {
		install()
		run()
		expect(windowListeners.get('keydown')).toHaveLength(1)
	})
})

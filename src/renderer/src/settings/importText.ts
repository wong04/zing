import type { BookmarkImportOutcome } from '@shared/ipc'

const bookmarks = (n: number): string => `${n} ${n === 1 ? 'bookmark' : 'bookmarks'}`

/** One sentence on what a bookmark import did. */
export function outcomeLine(o: BookmarkImportOutcome): string {
	const skipped = o.skipped.length
	if (o.addedToFolder === 0 && o.addedToBar === 0) return skipped > 0 ? `Nothing new to import; skipped ${skipped}.` : 'Nothing new to import.'
	const parts =
		o.addedToFolder > 0
			? [`Added ${bookmarks(o.addedToFolder)} to ${o.folderTitle}`, ...(o.addedToBar > 0 ? [`${o.addedToBar} to the favourites bar`] : [])]
			: [`Added ${bookmarks(o.addedToBar)} to the favourites bar`]
	return `${parts.join(', ')}${skipped > 0 ? `; skipped ${skipped}` : ''}.`
}

/** How to get a bookmarks file out of the browsers Zing can't read from this PC. */
export const fileHelp: readonly { app: string; steps: string }[] = [
	{ app: 'Dia', steps: 'Open the Bookmarks menu and choose Export Bookmarks. Dia saves an HTML file.' },
	{ app: 'Safari', steps: 'Choose File > Export > Bookmarks. Safari saves an HTML file.' },
	{ app: 'Firefox', steps: 'Open the Library (Ctrl+Shift+O), then Import and Backup > Export Bookmarks to HTML.' },
]

/** Shown in place of the Passwords file button while the vault is off. */
export const PASSWORDS_FILE_VAULT_OFF = 'Turn on “Offer to fill saved passwords” in Passwords to import a passwords file.'

export type PasswordImportState =
	| { step: 'idle' }
	| { step: 'confirm-vault' }
	| { step: 'waiting' }
	| { step: 'timed-out' }
	| { step: 'importing' }
	| { step: 'imported' }
	| { step: 'offer-recycle' }
	| { step: 'done' }
	| { step: 'error'; message: string }

export type PasswordImportEvent =
	| { type: 'begin'; vaultOn: boolean }
	| { type: 'confirm-vault' }
	| { type: 'cancel' }
	| { type: 'file-arrived' }
	| { type: 'timed-out' }
	| { type: 'choose-file' }
	| { type: 'imported' }
	| { type: 'continue' }
	| { type: 'recycled' }
	| { type: 'kept' }
	| { type: 'failed'; message: string }

export interface PasswordImportTransition {
	state: PasswordImportState
	/** The watch on the downloads folder is still running and has to end. */
	cancelWatch: boolean
}

export class InvalidPasswordImportEventError extends Error {
	override name = 'InvalidPasswordImportEventError'
}

const to = (step: Exclude<PasswordImportState['step'], 'error'>, cancelWatch = false): PasswordImportTransition => ({ state: { step }, cancelWatch })

/** The guided password import, one event at a time. An event the current step can't take is a bug in the caller, so it throws. */
export function passwordImportStep(state: PasswordImportState, event: PasswordImportEvent): PasswordImportTransition {
	const { step } = state
	if (event.type === 'failed') return { state: { step: 'error', message: event.message }, cancelWatch: step === 'waiting' }
	const moved = ((): PasswordImportTransition | undefined => {
		switch (event.type) {
			case 'begin':
				return step === 'idle' ? to(event.vaultOn ? 'waiting' : 'confirm-vault') : undefined
			case 'confirm-vault':
				return step === 'confirm-vault' ? to('waiting') : undefined
			case 'cancel':
				return step === 'confirm-vault' || step === 'waiting' || step === 'timed-out' || step === 'error' ? to('idle', step === 'waiting') : undefined
			case 'file-arrived':
				return step === 'waiting' ? to('importing') : undefined
			case 'timed-out':
				return step === 'waiting' ? to('timed-out') : undefined
			case 'choose-file':
				return step === 'waiting' || step === 'timed-out' ? to('importing', step === 'waiting') : undefined
			case 'imported':
				return step === 'importing' ? to('imported') : undefined
			case 'continue':
				return step === 'imported' ? to('offer-recycle') : undefined
			case 'recycled':
			case 'kept':
				return step === 'offer-recycle' ? to('done') : undefined
		}
	})()
	if (!moved) throw new InvalidPasswordImportEventError(`${event.type} can't happen at ${step}`)
	return moved
}

/** Whether the step can take the event. Late host events and finished awaits check this instead of tripping the strict reducer. */
export function passwordImportAccepts(state: PasswordImportState, event: PasswordImportEvent): boolean {
	try {
		passwordImportStep(state, event)
		return true
	} catch (failure) {
		if (failure instanceof InvalidPasswordImportEventError) return false
		throw failure
	}
}

/** A failure only matters while the step that was waiting on it is still current. */
export function passwordImportFailureApplies(state: PasswordImportState, during: PasswordImportState['step']): boolean {
	return state.step === during
}

export const CONFIRM_VAULT_COPY = 'Importing passwords turns on Zing’s password vault, which keeps them on this PC.'
export const EXPORT_BUTTON = 'Export passwords'
export const EXPORT_COPY_AFTER = ', confirm, and save. Zing will pick up the file.'
export const RECYCLE_COPY = 'The exported file has every password in plain text.'

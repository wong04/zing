import { useEffect, useState } from 'react'
import type { SyncState } from '@shared/vault/vault'
import { zing } from '../api'
import { InfoBanner, PillButton, PillField } from '../components'

type Strength = typeof import('@shared/vault/passwordStrength')

/** What went wrong, for the person. A refused host call carries the host's own sentence, after the channel. */
function messageOf(error: unknown): string {
	if (!(error instanceof Error)) throw error
	return error.message.replace(/^[a-z-]+:[a-z-]+: /, '')
}

/** zxcvbn and its English words are large, so they load only when Settings asks for a password. */
function useStrength(): Strength | undefined {
	const [strength, setStrength] = useState<Strength>()
	useEffect(() => {
		let current = true
		void import('@shared/vault/passwordStrength').then((loaded) => {
			if (current) setStrength(loaded)
		})
		return () => {
			current = false
		}
	}, [])
	return strength
}

/** Sync's state as the host pushes it; undefined until the first answer. */
export function useSyncState(): { sync: SyncState | undefined; error: string | undefined; setSync: (state: SyncState) => void } {
	const [sync, setSync] = useState<SyncState>()
	const [error, setError] = useState<string>()
	useEffect(() => {
		let current = true
		const unsubscribe = zing.onSyncState((state) => setSync(state))
		zing.getSyncState().then(
			(state) => {
				if (current) setSync(state)
			},
			(failure: unknown) => {
				if (current) setError(messageOf(failure))
			},
		)
		return () => {
			current = false
			unsubscribe()
		}
	}, [])
	return { sync, error, setSync }
}

const SCORE_WORDS = ['Very weak', 'Weak', 'Fair', 'Good', 'Strong'] as const

/** The renderer's zxcvbn verdict, for the meter only: the host checks the password again with its own. */
function StrengthMeter({ strength, password }: { strength: Strength | undefined; password: string }) {
	if (!strength || password === '') return null
	const score = strength.passwordScore(password)
	const enough = score >= strength.SYNC_PASSWORD_SCORE
	return (
		<div className="sync-strength" role="status" aria-live="polite">
			<meter min={0} max={4} value={score} aria-label="Password strength" />
			<span>
				{SCORE_WORDS[score]}. {enough ? 'Strong enough for sync.' : 'Sync needs a stronger password.'}
			</span>
		</div>
	)
}

/**
 * Turning on sync once signed in with Google: the Zing password, which becomes the sync password on every PC. One weaker than
 * the sync floor (zxcvbn 4) is changed first, in the same flow, with a generated five-word passphrase on offer.
 */
export function SyncSetup({ turnOn }: { turnOn: (password: string) => Promise<void> }) {
	const strength = useStrength()
	const [stronger, setStronger] = useState(false)
	const [current, setCurrent] = useState('')
	const [next, setNext] = useState('')
	const [again, setAgain] = useState('')
	const [suggested, setSuggested] = useState<string>()
	const [busy, setBusy] = useState(false)
	const [error, setError] = useState<string>()

	const run = (work: () => Promise<void>): void =>
		void (async () => {
			setError(undefined)
			setBusy(true)
			try {
				await work()
			} catch (failure) {
				setError(messageOf(failure))
			} finally {
				setBusy(false)
			}
		})()

	const submitCurrent = (): void =>
		run(async () => {
			try {
				await turnOn(current)
			} catch (failure) {
				if (failure instanceof Error && failure.name === 'SyncPasswordTooWeakException') {
					setStronger(true)
					return
				}
				throw failure
			}
		})

	const submitStronger = (): void =>
		run(async () => {
			if (next !== again) throw new Error('The two new passwords aren’t the same.')
			const outcome = await zing.changePassword(current, next)
			if (outcome.status === 'rejected') throw new Error(outcome.message)
			await turnOn(next)
		})

	const suggest = (): void => {
		if (!strength) return
		setSuggested(strength.generatePassphrase())
	}

	const takeSuggested = (): void => {
		if (!suggested) return
		setNext(suggested)
		setAgain(suggested)
	}

	if (!stronger) {
		return (
			<div className="sync-setup" role="form" aria-label="Turn on sync">
				<span>Enter your Zing password. It becomes your sync password on every PC, and unlocks your passwords on a new one.</span>
				<PillField type="password" label="Zing password" autoComplete="current-password" value={current} onChange={setCurrent} onSubmit={submitCurrent} />
				<StrengthMeter strength={strength} password={current} />
				<div className="vault-edit-actions">
					<PillButton size="small" disabled={busy || current === ''} onClick={submitCurrent}>
						Turn on sync
					</PillButton>
				</div>
				{error ? <InfoBanner kind="warning">{error}</InfoBanner> : null}
			</div>
		)
	}

	return (
		<div className="sync-setup" role="form" aria-label="Choose a stronger password">
			<InfoBanner kind="info">Sync needs a stronger Zing password, about 60 bits or more, because it protects your passwords on the server too. Five random words are easy to remember and strong enough.</InfoBanner>
			<div className="sync-suggestion">
				{suggested ? <code aria-label="Suggested passphrase">{suggested}</code> : null}
				<PillButton variant="outlined" size="small" disabled={!strength} onClick={suggest}>
					{suggested ? 'Another one' : 'Suggest a passphrase'}
				</PillButton>
				{suggested ? (
					<PillButton variant="outlined" size="small" onClick={takeSuggested}>
						Use it
					</PillButton>
				) : null}
			</div>
			<PillField type="password" label="New Zing password" autoComplete="new-password" value={next} onChange={setNext} />
			<StrengthMeter strength={strength} password={next} />
			<PillField type="password" label="New password again" autoComplete="new-password" value={again} onChange={setAgain} onSubmit={submitStronger} />
			<div className="vault-edit-actions">
				<PillButton variant="ghost" size="small" disabled={busy} onClick={() => setStronger(false)}>
					Back
				</PillButton>
				<PillButton size="small" disabled={busy || next === '' || again === ''} onClick={submitStronger}>
					Change password and turn on sync
				</PillButton>
			</div>
			{error ? <InfoBanner kind="warning">{error}</InfoBanner> : null}
		</div>
	)
}

/**
 * The recovery key, until the person confirms they saved it: Settings shows nothing else of the vault meanwhile. Right after
 * turning on it comes from that answer; later (after a restart) it is shown only after Windows Hello. It is never put on the clipboard.
 */
export function RecoveryKeyScreen({ recoveryKey, onConfirmed }: { recoveryKey: string | undefined; onConfirmed: (state: SyncState) => void }) {
	const [unsealed, setUnsealed] = useState<string>()
	const shown = recoveryKey ?? unsealed
	const [saved, setSaved] = useState(false)
	const [busy, setBusy] = useState(false)
	const [error, setError] = useState<string>()

	const run = (work: () => Promise<void>): void =>
		void (async () => {
			setError(undefined)
			setBusy(true)
			try {
				await work()
			} catch (failure) {
				setError(messageOf(failure))
			} finally {
				setBusy(false)
			}
		})()

	return (
		<div className="sync-recovery" role="alertdialog" aria-label="Save your recovery key" aria-describedby="sync-recovery-why">
			<strong>Save your recovery key</strong>
			<span id="sync-recovery-why">
				If you forget your Zing password, this key is the only way to get your synced passwords back. Write it down or print it, and keep it somewhere safe. Anyone with it and your Google sign-in can read
				your passwords.
			</span>
			{shown ? (
				<code className="sync-recovery-key" aria-label="Recovery key">
					{shown}
				</code>
			) : (
				<PillButton variant="outlined" size="small" disabled={busy} onClick={() => run(async () => setUnsealed((await zing.showRecoveryKey()).recoveryKey))}>
					Show recovery key
				</PillButton>
			)}
			<label className="sync-recovery-saved">
				<input type="checkbox" checked={saved} disabled={!shown} onChange={(e) => setSaved(e.target.checked)} />
				I&rsquo;ve saved or printed my recovery key
			</label>
			<div className="vault-edit-actions">
				<PillButton size="small" disabled={busy || !shown || !saved} onClick={() => run(async () => onConfirmed(await zing.confirmRecoverySaved()))}>
					Continue
				</PillButton>
			</div>
			{error ? <InfoBanner kind="warning">{error}</InfoBanner> : null}
		</div>
	)
}

/** With sync on: the recovery key again, after Windows Hello, hidden again on request. */
export function ShowRecoveryKey() {
	const [shown, setShown] = useState<string>()
	const [error, setError] = useState<string>()
	if (shown) {
		return (
			<div className="sync-suggestion">
				<code className="sync-recovery-key" aria-label="Recovery key">
					{shown}
				</code>
				<PillButton variant="ghost" size="small" onClick={() => setShown(undefined)}>
					Hide
				</PillButton>
			</div>
		)
	}
	return (
		<>
			<PillButton
				variant="outlined"
				size="small"
				onClick={() =>
					void zing.showRecoveryKey().then(
						(answer) => {
							setError(undefined)
							setShown(answer.recoveryKey)
						},
						(failure: unknown) => setError(messageOf(failure)),
					)
				}
			>
				Show recovery key
			</PillButton>
			{error ? <InfoBanner kind="warning">{error}</InfoBanner> : null}
		</>
	)
}

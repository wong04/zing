import { useEffect, useState } from 'react'
import type { SyncState } from '@shared/vault/vault'
import { zing } from '../api'
import { useAccount } from '../account/useAccount'
import { InfoBanner, PillButton, PillField } from '../components'

export type Strength = typeof import('@shared/vault/passwordStrength')

/** What went wrong, for the person. A refused host call carries the host's own sentence, after the channel. */
export function messageOf(error: unknown): string {
	if (!(error instanceof Error)) throw error
	return error.message.replace(/^[a-z-]+:[a-z-]+: /, '')
}

/** One action at a time: busy while it runs, then what it failed with, for the person, until the next one starts. */
export function useAction(): { busy: boolean; error: string | undefined; setError: (error: string | undefined) => void; run: (work: () => Promise<void>) => void } {
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
	return { busy, error, setError, run }
}

/** zxcvbn and its English words are large, so they load only when Settings asks for a password. */
export function useStrength(): Strength | undefined {
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

/**
 * The renderer's verdict, for the meter and its sentence only: the host checks the password again with its own. A refused password
 * never fills the meter past half, so it never looks as good as one that passes.
 */
export function StrengthMeter({ strength, password }: { strength: Strength | undefined; password: string }) {
	if (!strength || password === '') return null
	const verdict = strength.syncStrength(password)
	return (
		<div className="sync-strength" role="status" aria-live="polite">
			<meter min={0} max={4} value={verdict.enough ? verdict.score : Math.min(verdict.score, 2)} aria-label="Password strength" />
			<span>{strength.syncStrengthLabel(verdict)}</span>
		</div>
	)
}

/** Turn on sync's refusal when the Google account already syncs another PC's vault: this PC joins it instead. */
const SYNCS_ELSEWHERE = 'SyncAlreadySetUpElsewhereException'

/** Whether the signed-in Google account already syncs a vault; undefined while the host asks the server. */
function useServerHasKeys(): { hasKeys: boolean | undefined; setHasKeys: (has: boolean) => void; error: string | undefined; retry: () => void } {
	const [hasKeys, setHasKeys] = useState<boolean>()
	const [error, setError] = useState<string>()
	const [attempt, setAttempt] = useState(0)
	useEffect(() => {
		let current = true
		zing.syncServerHasKeys().then(
			(has) => {
				if (current) setHasKeys(has)
			},
			(failure: unknown) => {
				if (current) setError(messageOf(failure))
			},
		)
		return () => {
			current = false
		}
	}, [attempt])
	const retry = (): void => {
		setError(undefined)
		setAttempt(attempt + 1)
	}
	return { hasKeys, setHasKeys, error, retry }
}

/**
 * Turning on sync once signed in with Google. A Google account that already syncs another PC is joined with the password used
 * there (JoinSync): asking for a new one first would make a second account and vault the server then refuses. Otherwise the Zing
 * password, which becomes the sync password on every PC.
 */
export function SyncSetup({ turnOn, onJoined }: { turnOn: (password: string) => Promise<void>; onJoined: (state: SyncState) => void }) {
	const { hasKeys, setHasKeys, error, retry } = useServerHasKeys()
	if (error) {
		return (
			<div className="sync-setup" role="form" aria-label="Turn on sync">
				<InfoBanner kind="warning">{error}</InfoBanner>
				<div className="vault-edit-actions">
					<PillButton size="small" onClick={retry}>
						Try again
					</PillButton>
				</div>
			</div>
		)
	}
	if (hasKeys === undefined) {
		return (
			<div className="sync-setup" role="status" aria-live="polite">
				Checking whether this Google account already syncs passwords…
			</div>
		)
	}
	if (hasKeys) return <JoinSync onJoined={onJoined} />
	return <TurnOnSync turnOn={turnOn} syncsElsewhere={() => setHasKeys(true)} />
}

/**
 * This PC joins the sync of another PC with the Zing password used there, as Restore from sync does. With an account here, its own
 * password is asked for too, and becomes the sync one.
 */
function JoinSync({ onJoined }: { onJoined: (state: SyncState) => void }) {
	const account = useAccount()
	const [current, setCurrent] = useState('')
	const [password, setPassword] = useState('')
	const { busy, error, run } = useAction()
	if (!account) return null
	const join = (): void => run(async () => onJoined(await zing.joinSync(current, password)))
	return (
		<div className="sync-setup" role="form" aria-label="Join sync">
			<InfoBanner kind="info">
				This Google account already syncs passwords from another PC. Enter the Zing password you use there to bring them to this PC.
				{account.hasAccount ? ' It becomes your Zing password on this PC too.' : ' Your Zing account here is made from that one.'}
			</InfoBanner>
			{account.hasAccount ? (
				<PillField type="password" label="Zing password on this PC" autoComplete="current-password" value={current} onChange={setCurrent} />
			) : null}
			<PillField type="password" label="Sync password" autoComplete="off" value={password} onChange={setPassword} onSubmit={join} />
			<div className="vault-edit-actions">
				<PillButton size="small" disabled={busy || password === '' || (account.hasAccount && current === '')} onClick={join}>
					Join sync
				</PillButton>
			</div>
			{error ? <InfoBanner kind="warning">{error}</InfoBanner> : null}
		</div>
	)
}

/**
 * The Zing password, which becomes the sync password on every PC. One the host turns down (at least 12 characters, a number, a
 * symbol, and not common) is changed first, in the same flow, with a generated passphrase on offer. The host's sentence for the
 * first rule it failed says why.
 */
function TurnOnSync({ turnOn, syncsElsewhere }: { turnOn: (password: string) => Promise<void>; syncsElsewhere: () => void }) {
	const strength = useStrength()
	const [stronger, setStronger] = useState(false)
	const [current, setCurrent] = useState('')
	const [next, setNext] = useState('')
	const [again, setAgain] = useState('')
	const [suggested, setSuggested] = useState<string>()
	const [refusal, setRefusal] = useState<string>()
	const { busy, error, run } = useAction()

	const submitCurrent = (): void =>
		run(async () => {
			try {
				await turnOn(current)
			} catch (failure) {
				if (failure instanceof Error && failure.name === 'SyncPasswordTooWeakException') {
					setRefusal(messageOf(failure))
					setStronger(true)
					return
				}
				if (failure instanceof Error && failure.name === SYNCS_ELSEWHERE) {
					syncsElsewhere()
					return
				}
				throw failure
			}
		})

	const submitStronger = (): void =>
		run(async () => {
			if (next !== again) throw new Error('The two new passwords aren’t the same.')
			// Only so a refused password isn't made the account's first; the host checks it again when sync turns on.
			const failed = strength?.syncPasswordFailures(next)[0]
			if (strength && failed) throw new Error(strength.SYNC_PASSWORD_SENTENCES[failed])
			const outcome = await zing.changePassword(current, next)
			if (outcome.status === 'rejected') throw new Error(outcome.message)
			// The new password is the account's now, so a turn-on that fails is retried with it.
			setCurrent(next)
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
			<InfoBanner kind="info">
				{refusal} Sync protects your passwords on the server too, so its password needs at least 12 characters, a number and a symbol, and mustn’t be a common one.
			</InfoBanner>
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
	const { busy, error, run } = useAction()

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

/** As long as a revealed password stays shown (VaultSection's REVEAL_MS). */
const RECOVERY_REVEAL_MS = 30_000

/** With sync on: the recovery key again, after Windows Hello, hidden again on request. */
export function ShowRecoveryKey() {
	const [shown, setShown] = useState<string>()
	const [error, setError] = useState<string>()
	useEffect(() => {
		if (!shown) return
		const timer = setTimeout(() => setShown(undefined), RECOVERY_REVEAL_MS)
		return () => clearTimeout(timer)
	}, [shown])
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

import { useState } from 'react'
import type { AccountOutcome } from '@shared/shell/shellState'
import { zing } from '../api'
import { InfoBanner, PillButton, PillField, SegmentedControl } from '../components'
import { StrengthMeter, useAction, useStrength, useSyncState } from './SyncSetup'
import './settings.css'

export type RestoreWith = 'password' | 'recoveryKey'

type Step = 'unlock' | 'newPassword'

/** An account outcome as a thrown error, so a refusal shows like any other. */
function accepted(outcome: AccountOutcome): void {
	if (outcome.status === 'rejected') throw new Error(outcome.message)
}

/**
 * Restore from sync, from the account gate (spec, New PC or recovery): sign in with Google, then the Zing password or the recovery
 * key unlocks the synced passwords on this PC. A recovery key restore then asks for a new Zing password straight away, which must
 * pass the sync password rules like any sync password. Once the account is made or given its password the gate goes, and the logins come
 * with the next sync. The Google sign-in is the host's restore sign-in, held apart from the vault until a restore succeeds, so the
 * screen keeps it from signInToRestore rather than from the vault's sync state, which a failed step turning the vault on and off
 * again would reset.
 */
export function SyncRestore({ start, onBack }: { start: RestoreWith; onBack: () => void }) {
	const { sync, error: syncError } = useSyncState()
	const [signedInAs, setSignedInAs] = useState<string>()
	const strength = useStrength()
	const [using, setUsing] = useState<RestoreWith>(start)
	const [step, setStep] = useState<Step>('unlock')
	const [secret, setSecret] = useState('')
	const [next, setNext] = useState('')
	const [again, setAgain] = useState('')
	const { busy, error, run } = useAction()

	const signIn = (): void =>
		run(async () => {
			const restoring = await zing.signInToRestore()
			setSignedInAs(restoring.signedIn ? restoring.email : undefined)
		})

	const unlock = (): void =>
		run(async () => {
			if (using === 'password') {
				accepted(await zing.restoreWithPassword(secret))
				return
			}
			await zing.restoreWithRecoveryKey(secret)
			setSecret('')
			setStep('newPassword')
		})

	const setPassword = (): void =>
		run(async () => {
			if (next !== again) throw new Error('The two new passwords aren’t the same.')
			accepted(await zing.setPasswordAfterRecovery(next))
		})

	const suggest = (): void => {
		if (!strength) return
		const passphrase = strength.generatePassphrase()
		setNext(passphrase)
		setAgain(passphrase)
	}

	const shownError = error ?? syncError

	return (
		<div className="auth">
			<div className="auth-drag" />
			<div className="auth-card card-surface sync-restore" role="form" aria-label="Restore from sync">
				<div className="auth-header">
					<div className="auth-mark" aria-hidden="true">
						Z
					</div>
					<h1>{step === 'newPassword' ? 'Choose a new Zing password' : 'Restore from sync'}</h1>
					<p>
						{step === 'newPassword'
							? 'Your recovery key worked. Choose a new Zing password: it becomes your sync password on every PC, and your old one stops working.'
							: 'Bring back your saved passwords from another PC.'}
					</p>
				</div>

				{step === 'unlock' && signedInAs === undefined ? (
					<PillButton size="large" disabled={busy || !sync} onClick={signIn}>
						Sign in with Google
					</PillButton>
				) : null}

				{step === 'unlock' && signedInAs !== undefined ? (
					<>
						<span>Signed in as {signedInAs}.</span>
						<SegmentedControl<RestoreWith>
							aria-label="Unlock with"
							value={using}
							onChange={(value) => {
								setUsing(value)
								setSecret('')
							}}
							options={[
								{ value: 'password', label: 'Zing password' },
								{ value: 'recoveryKey', label: 'Recovery key' },
							]}
						/>
						<PillField
							type={using === 'password' ? 'password' : 'text'}
							label={using === 'password' ? 'Zing password' : 'Recovery key'}
							autoComplete="off"
							spellCheck={false}
							value={secret}
							onChange={setSecret}
							onSubmit={unlock}
							autoFocus
						/>
						<PillButton size="large" disabled={busy || secret === ''} onClick={unlock}>
							Restore
						</PillButton>
					</>
				) : null}

				{step === 'newPassword' ? (
					<>
						<div className="sync-suggestion">
							<PillButton variant="outlined" size="small" disabled={!strength} onClick={suggest}>
								Suggest a passphrase
							</PillButton>
						</div>
						<PillField type="password" label="New Zing password" autoComplete="new-password" value={next} onChange={setNext} autoFocus />
						<StrengthMeter strength={strength} password={next} />
						<PillField type="password" label="New password again" autoComplete="new-password" value={again} onChange={setAgain} onSubmit={setPassword} />
						<PillButton size="large" disabled={busy || next === '' || again === ''} onClick={setPassword}>
							Set password
						</PillButton>
					</>
				) : null}

				{shownError ? <InfoBanner kind="warning">{shownError}</InfoBanner> : null}

				{step === 'unlock' ? (
					<div className="auth-links">
						<PillButton variant="ghost" size="small" disabled={busy} onClick={onBack}>
							Back
						</PillButton>
					</div>
				) : null}
			</div>
		</div>
	)
}

import { useState, type FormEvent } from 'react'
import type { AccountOutcome, AccountState } from '@shared/shell/shellState'
import { zing } from '../api'
import { InfoBanner, PillButton, PillField } from '../components'
import { LockIcon, MailIcon, PersonIcon, TextIcon } from '../components/icons'
import { SyncRestore, type RestoreWith } from '../settings/SyncRestore'
import { messageOf, useSyncState } from '../settings/SyncSetup'
import { StartOver } from './StartOver'
import { initials } from './useAccount'
import './account.css'

type Mode = 'signIn' | 'signUp'

/**
 * The sign-in gate (Krackn AuthView): a local account, or one restored from sync. With sync on, a forgotten password is reset with
 * the recovery key (Restore from sync), never on this PC alone, since the synced keys would stay wrapped by the old one.
 */
export function AuthView({ account }: { account: AccountState }) {
	const { sync } = useSyncState()
	const [restoring, setRestoring] = useState<RestoreWith>()
	const [startingOver, setStartingOver] = useState(false)
	const [mode, setMode] = useState<Mode>(account.hasAccount ? 'signIn' : 'signUp')
	const [username, setUsername] = useState('')
	const [displayName, setDisplayName] = useState('')
	const [email, setEmail] = useState('')
	const [password, setPassword] = useState('')
	const [confirm, setConfirm] = useState('')
	const [error, setError] = useState<string>()

	const resetting = account.needsPasswordReset && mode === 'signIn'
	const syncOn = sync?.on === true

	const finish = (outcome: AccountOutcome): void => {
		if (outcome.status === 'rejected') setError(outcome.message)
	}

	const submit = (e: FormEvent): void => {
		e.preventDefault()
		setError(undefined)
		if (mode === 'signUp') {
			if (password !== confirm) {
				setError("The two passwords don't match.")
				return
			}
			// With sync on (keys.bin says so), the host refuses a new account and says why (final review I2).
			void zing.signUp(username, displayName, email, password).then(finish, (failure: unknown) => setError(messageOf(failure)))
			return
		}
		if (!resetting) {
			void zing.signIn(password).then(finish)
			return
		}
		void zing.resetPassword(password).then(finish, (failure: unknown) => {
			// keys.bin says sync is on even when this window hasn't heard it yet: the recovery key resets it.
			if (failure instanceof Error && failure.name === 'SyncNeedsRecoveryKeyException') {
				setRestoring('recoveryKey')
				return
			}
			setError(failure instanceof Error ? failure.message : String(failure))
		})
	}

	const switchMode = (): void => {
		setMode(mode === 'signUp' ? 'signIn' : 'signUp')
		setError(undefined)
	}

	if (restoring) return <SyncRestore start={restoring} onBack={() => setRestoring(undefined)} />
	if (startingOver) return <StartOver onBack={() => setStartingOver(false)} />

	const startOverLink = (
		<PillButton variant="ghost" size="small" onClick={() => setStartingOver(true)}>
			Forgot your password? Start over
		</PillButton>
	)

	if (resetting && syncOn) {
		return (
			<div className="auth">
				<div className="auth-drag" />
				<div className="auth-card card-surface" role="form" aria-label="Reset your password">
					<div className="auth-header">
						<div className="auth-mark" aria-hidden="true">
							Z
						</div>
						<h1>Welcome back</h1>
					</div>
					<InfoBanner kind="info">
						<strong>Use your recovery key.</strong> The saved password check can&rsquo;t be read on this Windows account, and sync is on, so
						choose a new password with your recovery key. It changes your password for sync too.
					</InfoBanner>
					<PillButton size="large" onClick={() => setRestoring('recoveryKey')}>
						Use recovery key
					</PillButton>
					<div className="auth-links">{startOverLink}</div>
				</div>
			</div>
		)
	}

	return (
		<div className="auth">
			<div className="auth-drag" />
			<form className="auth-card card-surface" onSubmit={submit} aria-labelledby="auth-title">
				<div className="auth-header">
					<div className="auth-mark" aria-hidden="true">
						Z
					</div>
					<h1 id="auth-title">{mode === 'signUp' ? 'Create your account' : 'Welcome back'}</h1>
					<p>
						{mode === 'signUp'
							? 'So Zing remembers your setup and preferences.'
							: resetting
								? 'Choose a password to finish restoring your account.'
								: account.passwordChangedElsewhere
									? 'Your Zing password was changed on another PC. Enter the new one.'
									: 'Sign in to pick up where you left off.'}
					</p>
				</div>

				{mode === 'signUp' ? (
					<>
						<PillField label="Username" value={username} onChange={setUsername} icon={<PersonIcon size={14} />} placeholder="jdoe" autoComplete="off" autoFocus />
						<PillField label="Display name" value={displayName} onChange={setDisplayName} icon={<TextIcon size={14} />} placeholder="Jane Doe" autoComplete="off" />
						<PillField label="Email (optional)" value={email} onChange={setEmail} icon={<MailIcon size={14} />} placeholder="jane@example.com" autoComplete="off" />
						<PillField label="Password" type="password" value={password} onChange={setPassword} icon={<LockIcon size={14} />} placeholder="at least 8 characters" autoComplete="new-password" />
						<PillField label="Confirm" type="password" value={confirm} onChange={setConfirm} icon={<LockIcon size={14} />} placeholder="repeat it" autoComplete="new-password" />
					</>
				) : (
					<>
						{account.profile ? (
							<div className="auth-who">
								<span className="avatar" aria-hidden="true">
									{initials(account.profile.displayName)}
								</span>
								<span>
									<strong>{account.profile.displayName}</strong>
									<small>@{account.profile.username}</small>
								</span>
							</div>
						) : null}
						<PillField label="Password" type="password" value={password} onChange={setPassword} icon={<LockIcon size={14} />} placeholder="your password" autoComplete="current-password" autoFocus />
					</>
				)}

				{resetting ? (
					<InfoBanner kind="info">
						<strong>Set your password again.</strong> The saved password check can't be read on this Windows account, so your
						profile and settings are intact but you need to choose a password.
					</InfoBanner>
				) : null}
				{error ? <InfoBanner kind="warning">{error}</InfoBanner> : null}

				<PillButton type="submit" size="large">
					{mode === 'signUp' ? 'Create Account' : resetting ? 'Set Password' : 'Sign In'}
				</PillButton>
				<div className="auth-links">
					{account.hasAccount ? (
						<PillButton variant="ghost" size="small" onClick={switchMode}>
							{mode === 'signUp' ? 'I have an account' : 'Create new'}
						</PillButton>
					) : null}
					{account.hasAccount && mode === 'signIn' && syncOn ? (
						<PillButton variant="ghost" size="small" onClick={() => setRestoring('recoveryKey')}>
							Forgot password?
						</PillButton>
					) : null}
					{account.hasAccount && mode === 'signIn' ? startOverLink : null}
					<PillButton variant="ghost" size="small" onClick={() => setRestoring('password')}>
						Restore from sync
					</PillButton>
					<PillButton variant="ghost" size="small" onClick={() => void zing.continueAsGuest()}>
						Skip for now
					</PillButton>
				</div>
				<p className="auth-fine">
					Your account stays on this PC. The password is kept only as a one-way check, sealed with the Windows key store.
					Only sync uploads anything, and only encrypted.
				</p>
			</form>
		</div>
	)
}

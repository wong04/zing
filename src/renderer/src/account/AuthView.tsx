import { useState, type FormEvent } from 'react'
import type { AccountOutcome, AccountState } from '@shared/shell/shellState'
import { zing } from '../api'
import { InfoBanner, PillButton, PillField } from '../components'
import { LockIcon, MailIcon, PersonIcon, TextIcon } from '../components/icons'
import { initials } from './useAccount'
import './account.css'

type Mode = 'signIn' | 'signUp'

/** The sign-in gate (Krackn AuthView): a local account, nothing uploaded anywhere. */
export function AuthView({ account }: { account: AccountState }) {
	const [mode, setMode] = useState<Mode>(account.hasAccount ? 'signIn' : 'signUp')
	const [username, setUsername] = useState('')
	const [displayName, setDisplayName] = useState('')
	const [email, setEmail] = useState('')
	const [password, setPassword] = useState('')
	const [confirm, setConfirm] = useState('')
	const [error, setError] = useState<string>()

	const resetting = account.needsPasswordReset && mode === 'signIn'

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
			void zing.signUp(username, displayName, email, password).then(finish)
			return
		}
		void (resetting ? zing.resetPassword(password) : zing.signIn(password)).then(finish)
	}

	const switchMode = (): void => {
		setMode(mode === 'signUp' ? 'signIn' : 'signUp')
		setError(undefined)
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
					<PillButton variant="ghost" size="small" onClick={() => void zing.continueAsGuest()}>
						Skip for now
					</PillButton>
				</div>
				<p className="auth-fine">
					Your account stays on this PC. The password is kept only as a one-way check, sealed with the Windows key store.
					There is no server, so nothing is uploaded.
				</p>
			</form>
		</div>
	)
}

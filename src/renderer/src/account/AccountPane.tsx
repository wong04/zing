import { useState } from 'react'
import { zing } from '../api'
import { PillButton, PillField, Toggle } from '../components'
import { SettingRowShell } from '../settings/SettingRowShell'
import { initials, useAccount } from './useAccount'

/** Settings › Account: the profile, the launch lock, sign out and delete (Krackn accountPane). */
export function AccountPane() {
	const account = useAccount()
	const [editing, setEditing] = useState(false)
	const [name, setName] = useState('')
	const [email, setEmail] = useState('')
	const [confirmDelete, setConfirmDelete] = useState(false)
	if (!account) return null
	const user = account.profile
	if (!user) {
		return (
			<SettingRowShell title="No account" detail="Create one to keep your profile and preferences.">
				<PillButton size="small" onClick={() => void zing.signOut()}>
					Create Account
				</PillButton>
			</SettingRowShell>
		)
	}

	const startEdit = (): void => {
		setName(user.displayName)
		setEmail(user.email)
		setEditing(true)
	}
	const save = (): void => {
		void zing.updateProfile(name, email)
		setEditing(false)
	}

	return (
		<>
			<div className="account-head">
				<span className="avatar large" aria-hidden="true">
					{initials(user.displayName)}
				</span>
				<span className="account-head-text">
					<strong>{user.displayName}</strong>
					<small>{user.email === '' ? `@${user.username}` : user.email}</small>
				</span>
				{editing ? null : (
					<PillButton variant="outlined" size="small" onClick={startEdit}>
						Edit
					</PillButton>
				)}
			</div>
			{editing ? (
				<div className="account-edit">
					<PillField value={name} onChange={setName} placeholder="Display name" aria-label="Display name" />
					<PillField value={email} onChange={setEmail} placeholder="Email" aria-label="Email" />
					<div className="account-edit-actions">
						<PillButton size="small" onClick={save}>
							Save
						</PillButton>
						<PillButton variant="ghost" size="small" onClick={() => setEditing(false)}>
							Cancel
						</PillButton>
					</div>
				</div>
			) : null}
			<SettingRowShell title="Ask for password at launch">
				<Toggle
					aria-label="Ask for password at launch"
					checked={account.requirePasswordOnLaunch}
					onChange={(required) => void zing.setRequirePassword(required)}
				/>
			</SettingRowShell>
			<SettingRowShell title="Sign out">
				<PillButton variant="outlined" size="small" onClick={() => void zing.signOut()}>
					Sign Out
				</PillButton>
			</SettingRowShell>
			<SettingRowShell
				title="Delete account"
				detail={confirmDelete ? 'Removes your profile and its saved password check. Your files are kept.' : undefined}
				warning={confirmDelete}
			>
				{confirmDelete ? (
					<span className="row-actions">
						<PillButton variant="ghost" size="small" onClick={() => setConfirmDelete(false)}>
							Keep
						</PillButton>
						<PillButton size="small" onClick={() => void zing.deleteAccount()}>
							Delete
						</PillButton>
					</span>
				) : (
					<PillButton variant="ghost" size="small" onClick={() => setConfirmDelete(true)}>
						Delete…
					</PillButton>
				)}
			</SettingRowShell>
		</>
	)
}

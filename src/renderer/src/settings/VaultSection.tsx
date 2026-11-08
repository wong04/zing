import { useEffect, useState } from 'react'
import { generatePassword } from '@shared/devtools/password'
import { recentlyDeleted, type LoginSummary, type PasswordHistoryEntry, type SyncState, type VaultImportResult } from '@shared/vault/vault'
import { zing } from '../api'
import { InfoBanner, PillButton, PillField } from '../components'
import { SettingRowShell } from './SettingRowShell'
import { useVault } from './useVaultData'

/** A password stays shown for this long, then hides itself. */
const REVEAL_MS = 30_000

const GENERATED = { length: 20, uppercase: true, digits: true, symbols: true } as const

const siteName = (login: LoginSummary): string => login.title || new URL(login.origin).host

const dateOf = (ms: number): string => new Date(ms).toLocaleString()

function matches(login: LoginSummary, query: string): boolean {
	const needle = query.trim().toLowerCase()
	if (needle === '') return true
	return [login.origin, login.title, login.username, login.note].some((field) => field.toLowerCase().includes(needle))
}

/** What went wrong, for the person. A refused host call carries the host's own sentence. */
function messageOf(error: unknown): string {
	if (!(error instanceof Error)) throw error
	// The bridge puts the channel in front ("vault:reveal: ..."); the person doesn't need it.
	return error.message.replace(/^[a-z-]+:[a-z-]+: /, '')
}

interface Revealed {
	/** A login's id, or `${id}/${index}` for an old password. */
	readonly key: string
	readonly password: string
}

function EditForm({ login, onDone, run }: { login: LoginSummary; onDone: () => void; run: Run }) {
	const [title, setTitle] = useState(login.title)
	const [username, setUsername] = useState(login.username)
	const [note, setNote] = useState(login.note)
	const [password, setPassword] = useState('')
	const [shown, setShown] = useState(false)
	const [history, setHistory] = useState<readonly PasswordHistoryEntry[]>([])
	const [old, setOld] = useState<Revealed>()

	useEffect(() => {
		let current = true
		void zing.loginHistory(login.id).then((entries) => {
			if (current) setHistory(entries)
		})
		return () => {
			current = false
		}
	}, [login.id, login.passwordUpdatedAt])

	useEffect(() => {
		if (!old) return
		const timer = setTimeout(() => setOld(undefined), REVEAL_MS)
		return () => clearTimeout(timer)
	}, [old])

	const save = (): void =>
		void run(async () => {
			await zing.updateLogin(login.id, { title, username, note, ...(password === '' ? {} : { password }) })
			onDone()
		})

	const generate = (): void => {
		setPassword(generatePassword(GENERATED))
		setShown(true)
	}

	return (
		<form
			className="vault-edit"
			aria-label={`Edit ${siteName(login)}`}
			onSubmit={(e) => {
				e.preventDefault()
				save()
			}}
		>
			<div className="vault-edit-site">{login.origin}</div>
			<PillField label="Name" value={title} onChange={setTitle} placeholder={new URL(login.origin).host} autoFocus />
			<PillField label="Username" value={username} onChange={setUsername} autoComplete="off" />
			<div className="vault-password-field">
				<PillField
					label="Password"
					type={shown ? 'text' : 'password'}
					value={password}
					onChange={setPassword}
					placeholder="Leave empty to keep the current one"
					autoComplete="new-password"
				/>
				<PillButton variant="outlined" size="small" onClick={generate}>
					Generate
				</PillButton>
				<PillButton variant="ghost" size="small" aria-label={shown ? 'Hide password' : 'Show password'} onClick={() => setShown(!shown)}>
					{shown ? 'Hide' : 'Show'}
				</PillButton>
			</div>
			<PillField label="Note" value={note} onChange={setNote} autoComplete="off" />
			{history.length > 0 ? (
				<div className="vault-history" role="group" aria-label="Earlier passwords">
					<div className="vault-history-title">Earlier passwords</div>
					{history.map((entry, index) => {
						const key = `${login.id}/${index}`
						const shownOld = old?.key === key
						return (
							<div key={`${entry.replacedAt}/${index}`} className="vault-history-row">
								<span>Replaced {dateOf(entry.replacedAt)}</span>
								{shownOld ? <code className="vault-secret">{old.password}</code> : null}
								<PillButton
									variant="ghost"
									size="small"
									aria-label={`${shownOld ? 'Hide' : 'Show'} earlier password from ${dateOf(entry.replacedAt)}`}
									onClick={() =>
										shownOld ? setOld(undefined) : void run(async () => setOld({ key, password: await zing.revealOldPassword(login.id, index) }))
									}
								>
									{shownOld ? 'Hide' : 'Show'}
								</PillButton>
							</div>
						)
					})}
				</div>
			) : null}
			<div className="vault-edit-actions">
				<PillButton variant="ghost" size="small" onClick={onDone}>
					Cancel
				</PillButton>
				<PillButton size="small" type="submit">
					Save
				</PillButton>
			</div>
		</form>
	)
}

type Run = (work: () => Promise<void>) => Promise<void>

function LoginRow({ login, revealed, editing, run, notify, setRevealed, setEditing }: {
	login: LoginSummary
	revealed: Revealed | undefined
	editing: boolean
	run: Run
	notify: (text: string) => void
	setRevealed: (value: Revealed | undefined) => void
	setEditing: (id: string | undefined) => void
}) {
	const name = siteName(login)
	const shown = revealed?.key === login.id
	if (editing) return <li className="vault-row editing"><EditForm login={login} onDone={() => setEditing(undefined)} run={run} /></li>
	return (
		<li className="vault-row">
			<span className="vault-text">
				<strong>{name}</strong>
				<small>
					{login.username === '' ? 'No username' : login.username} · {new URL(login.origin).host}
				</small>
				{login.note === '' ? null : <small>{login.note.split(/\r?\n/)[0]}</small>}
				{shown ? <code className="vault-secret">{revealed.password}</code> : null}
			</span>
			<PillButton
				variant="ghost"
				size="small"
				aria-label={`${shown ? 'Hide' : 'Show'} password for ${name}`}
				onClick={() => (shown ? setRevealed(undefined) : void run(async () => setRevealed({ key: login.id, password: await zing.revealPassword(login.id) })))}
			>
				{shown ? 'Hide' : 'Show'}
			</PillButton>
			<PillButton
				variant="ghost"
				size="small"
				aria-label={`Copy password for ${name}`}
				onClick={() =>
					void run(async () => {
						await zing.copyPassword(login.id)
						notify('Copied. Zing clears the clipboard in 30 seconds unless you copy something else first.')
					})
				}
			>
				Copy
			</PillButton>
			<PillButton variant="ghost" size="small" aria-label={`Edit ${name}`} onClick={() => setEditing(login.id)}>
				Edit
			</PillButton>
			<PillButton variant="ghost" size="small" aria-label={`Delete ${name}`} onClick={() => void run(() => zing.deleteLogin(login.id))}>
				Delete
			</PillButton>
		</li>
	)
}

/** Sync's sign-in: Turn on sync signs in with Google in the browser; signed in, it shows the account and a Sign out. Logins sync in later tasks. */
function SyncRow() {
	const [sync, setSync] = useState<SyncState>()
	const [error, setError] = useState<string>()
	const [busy, setBusy] = useState(false)

	useEffect(() => {
		let current = true
		void zing.getSyncState().then((state) => {
			if (current) setSync(state)
		})
		return () => {
			current = false
		}
	}, [])

	if (!sync) return null

	const act = (work: () => Promise<SyncState>): void =>
		void (async () => {
			setError(undefined)
			setBusy(true)
			try {
				setSync(await work())
			} catch (failure) {
				setError(messageOf(failure))
			} finally {
				setBusy(false)
			}
		})()

	return (
		<div className="vault-sync" role="group" aria-label="Sync">
			<SettingRowShell
				title="Sync"
				detail={sync.signedIn ? `Signed in as ${sync.email ?? ''}.` : busy ? 'Finish signing in with Google in your browser.' : 'Keep saved passwords in step across your PCs. The server only ever sees them encrypted.'}
			>
				{sync.signedIn ? (
					<PillButton variant="outlined" size="small" disabled={busy} onClick={() => act(() => zing.signOutOfSync())}>
						Sign out
					</PillButton>
				) : (
					<PillButton variant="outlined" size="small" disabled={busy} onClick={() => act(() => zing.signInToSync())}>
						Turn on sync
					</PillButton>
				)}
			</SettingRowShell>
			{error ? <InfoBanner kind="warning">{error}</InfoBanner> : null}
		</div>
	)
}

/** The manage page for the password vault (Settings, Browsing): search, show, copy, edit, delete, import and export. */
export function VaultSection() {
	const vault = useVault()
	const [query, setQuery] = useState('')
	const [revealed, setRevealed] = useState<Revealed>()
	const [editing, setEditing] = useState<string>()
	const [error, setError] = useState<string>()
	const [notice, setNotice] = useState<string>()
	const [imported, setImported] = useState<VaultImportResult>()
	const [confirmingExport, setConfirmingExport] = useState(false)
	const [busy, setBusy] = useState(false)
	// When the page opened: Recently deleted drops a login 30 days after it was deleted.
	const [now] = useState(() => Date.now())

	useEffect(() => {
		if (!revealed) return
		const timer = setTimeout(() => setRevealed(undefined), REVEAL_MS)
		return () => clearTimeout(timer)
	}, [revealed])

	if (!vault) return null
	const { state, summaries, neverSaves } = vault
	if (state.error === 'unreadable') {
		return <InfoBanner kind="warning">Zing couldn&rsquo;t read the saved-passwords file, so the vault is off. The file has not been changed or removed.</InfoBanner>
	}
	if (!state.enabled) return null

	const run: Run = async (work) => {
		setError(undefined)
		setNotice(undefined)
		try {
			await work()
		} catch (failure) {
			setError(messageOf(failure))
		}
	}
	const guarded: Run = async (work) => {
		setBusy(true)
		try {
			await run(work)
		} finally {
			setBusy(false)
		}
	}

	const importCsv = (): void =>
		void guarded(async () => {
			setImported(undefined)
			const [file] = await zing.pickFiles(['csv'])
			if (!file) return
			setImported(await zing.importPasswordsCsv(file.token))
		})

	const exportCsv = (): void =>
		void guarded(async () => {
			setConfirmingExport(false)
			const saved = await zing.exportPasswordsCsv()
			setNotice(`Saved to Downloads as ${saved.name}.`)
		})

	const logins = summaries.filter((s) => s.deletedAt === null)
	const shownLogins = logins.filter((s) => matches(s, query)).sort((a, b) => siteName(a).localeCompare(siteName(b)))
	const deleted = recentlyDeleted(summaries, now)

	return (
		<div className="vault" role="group" aria-label="Saved passwords">
			<SettingRowShell title="Saved passwords" detail={logins.length === 1 ? '1 login. Windows Hello is asked before a password is shown.' : `${logins.length} logins. Windows Hello is asked before a password is shown.`}>
				<div className="vault-actions">
					<PillButton variant="outlined" size="small" disabled={busy} onClick={importCsv}>
						Import CSV
					</PillButton>
					<PillButton variant="outlined" size="small" disabled={busy || logins.length === 0} onClick={() => setConfirmingExport(true)}>
						Export CSV
					</PillButton>
				</div>
			</SettingRowShell>

			<SyncRow />

			{!state.helloAvailable ? (
				<InfoBanner kind="warning">Windows Hello isn&rsquo;t set up on this PC. You can look through your logins, but passwords can&rsquo;t be shown, saved or changed until it is.</InfoBanner>
			) : null}
			{error ? <InfoBanner kind="warning">{error}</InfoBanner> : null}
			{notice ? <InfoBanner kind="info">{notice}</InfoBanner> : null}

			{confirmingExport ? (
				<div className="vault-confirm" role="alertdialog" aria-label="Export passwords" aria-describedby="vault-export-warning">
					<InfoBanner kind="warning">
						<span id="vault-export-warning">
							The file will hold every password as plain text. Anyone who gets it can read them, and Zing can&rsquo;t protect it once it is saved. Delete it when you&rsquo;re done.
							It is for importing into a password manager, not for opening in a spreadsheet.
						</span>
					</InfoBanner>
					<div className="vault-edit-actions">
						<PillButton variant="ghost" size="small" autoFocus onClick={() => setConfirmingExport(false)}>
							Cancel
						</PillButton>
						<PillButton size="small" disabled={busy} onClick={exportCsv}>
							Save CSV to Downloads
						</PillButton>
					</div>
				</div>
			) : null}

			{imported ? (
				<div className="vault-import" role="status">
					<InfoBanner kind="info">
						Added {imported.added}, merged {imported.merged} into logins you already had, skipped {imported.skipped.length}.
					</InfoBanner>
					{imported.skipped.length > 0 ? (
						<ul className="vault-skipped" aria-label="Skipped rows">
							{imported.skipped.map((row) => (
								<li key={row.row}>
									Row {row.row}: {row.reason}
								</li>
							))}
						</ul>
					) : null}
				</div>
			) : null}

			{logins.length === 0 ? (
				<div className="vault-empty">
					<strong>No saved passwords yet</strong>
					<span>Sign in to a site and Zing offers to save it, or import a CSV from another browser.</span>
				</div>
			) : (
				<>
					<PillField aria-label="Search saved passwords" placeholder="Search by site or username" value={query} onChange={setQuery} />
					<ul className="vault-list" aria-label="Saved logins">
						{shownLogins.map((login) => (
							<LoginRow key={login.id} login={login} revealed={revealed} editing={editing === login.id} run={guarded} notify={setNotice} setRevealed={setRevealed} setEditing={setEditing} />
						))}
					</ul>
					{shownLogins.length === 0 ? <div className="vault-none">No login matches &ldquo;{query.trim()}&rdquo;.</div> : null}
				</>
			)}

			{deleted.length > 0 ? (
				<>
					<div className="vault-subtitle">Recently deleted</div>
					<ul className="vault-list" aria-label="Recently deleted">
						{deleted.map((login) => (
							<li key={login.id} className="vault-row">
								<span className="vault-text">
									<strong>{siteName(login)}</strong>
									<small>
										{login.username === '' ? 'No username' : login.username} · deleted {dateOf(login.deletedAt ?? 0)}
									</small>
								</span>
								<PillButton variant="ghost" size="small" aria-label={`Restore ${siteName(login)}`} onClick={() => void guarded(() => zing.restoreLogin(login.id))}>
									Restore
								</PillButton>
							</li>
						))}
					</ul>
					<div className="vault-none">Zing removes these for good 30 days after they were deleted.</div>
				</>
			) : null}

			{neverSaves.length > 0 ? (
				<>
					<div className="vault-subtitle">Never saved</div>
					<ul className="vault-list" aria-label="Never saved">
						{neverSaves.map((site) => (
							<li key={site.origin} className="vault-row">
								<span className="vault-text">
									<strong>{new URL(site.origin).host}</strong>
									<small>Zing doesn&rsquo;t offer to save passwords here.</small>
								</span>
								<PillButton variant="ghost" size="small" aria-label={`Remove ${new URL(site.origin).host} from never saved`} onClick={() => void guarded(() => zing.removeNeverSave(site.origin))}>
									Remove
								</PillButton>
							</li>
						))}
					</ul>
				</>
			) : null}
		</div>
	)
}

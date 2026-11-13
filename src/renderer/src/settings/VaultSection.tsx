import { useEffect, useState } from 'react'
import { generatePassword } from '@shared/devtools/password'
import { recentlyDeleted, type LoginSummary, type PasswordHistoryEntry, type SyncNotice, type SyncState } from '@shared/vault/vault'
import { zing } from '../api'
import { InfoBanner, PillButton, PillField } from '../components'
import { SettingRowShell } from './SettingRowShell'
import { RecoveryKeyScreen, ShowRecoveryKey, SyncSetup, useAction, useSyncState } from './SyncSetup'
import { signInLabel, syncBanner, syncControls, syncDetail, TURN_OFF_SYNC_CONFIRM, UPLOAD_LOCAL_CONFIRM } from './syncBanner'
import { messageOf, PasswordImportResult, usePasswordCsvImport } from './PasswordImport'
import { useVault } from './useVaultData'
import { earlierPasswords, loginsDetail } from './vaultText'

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

interface Revealed {
	/** A login's id, or `${id}/${index}` for an old password. */
	readonly key: string
	readonly password: string
}

/** Earlier passwords, newest first: each is shown on request, after Windows Hello, and hides itself after REVEAL_MS. */
function PasswordHistory({ login, run }: { login: LoginSummary; run: Run }) {
	const [history, setHistory] = useState<readonly PasswordHistoryEntry[]>([])
	const [old, setOld] = useState<Revealed>()

	// historyCount too: a merge from another PC can add an earlier password without changing this one.
	useEffect(() => {
		let current = true
		void zing.loginHistory(login.id).then((entries) => {
			if (current) setHistory(entries)
		})
		return () => {
			current = false
		}
	}, [login.id, login.passwordUpdatedAt, login.historyCount])

	useEffect(() => {
		if (!old) return
		const timer = setTimeout(() => setOld(undefined), REVEAL_MS)
		return () => clearTimeout(timer)
	}, [old])

	// Nothing until the list arrives, so no empty "Earlier passwords" box flashes (review M2).
	if (history.length === 0) return null
	return (
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
	)
}

function EditForm({ login, onDone, run }: { login: LoginSummary; onDone: () => void; run: Run }) {
	const [title, setTitle] = useState(login.title)
	const [username, setUsername] = useState(login.username)
	const [note, setNote] = useState(login.note)
	const [password, setPassword] = useState('')
	const [shown, setShown] = useState(false)
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
			{login.historyCount > 0 ? <PasswordHistory login={login} run={run} /> : null}
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
	const [historyOpen, setHistoryOpen] = useState(false)
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
				{login.historyCount > 0 ? (
					<span className="vault-history-toggle">
						<PillButton variant="ghost" size="small" aria-expanded={historyOpen} onClick={() => setHistoryOpen(!historyOpen)}>
							{earlierPasswords(login.historyCount)}
						</PillButton>
					</span>
				) : null}
				{historyOpen && login.historyCount > 0 ? <PasswordHistory login={login} run={run} /> : null}
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

const SYNC_NOTICES: Record<SyncNotice, string> = {
	'signed-out': 'Signed out of sync, sign in again.',
	unreadable: 'This PC\u2019s sync sign-in can\u2019t be read.',
	'kept-on-server': 'Signed out on this PC. Zing couldn\u2019t reach the sync server, so its copy of the sign-in expires within 90 days.',
}

/**
 * Sync: Turn on sync signs in with Google in the browser, with Cancel while it waits, then asks for the Zing password to turn it
 * on (SyncSetup); on, it shows the account, Show recovery key and Sign out. A sign-in the server ended, or one this PC can't read,
 * says so and offers Sign in again. A sync that stopped says why, and after the server went back or held a login back, offers
 * Upload this PC's copy, which says what it does before it runs. While keys.bin holds sync keys, Turn off sync on this PC (Ruling 68)
 * sits beside Sign out or Turn on sync, and says what it does before it runs.
 */
function SyncRow({ sync, setSync, turnOn }: { sync: SyncState; setSync: (state: SyncState) => void; turnOn: (password: string) => Promise<void> }) {
	const { busy: acting, error: actionError, setError, run } = useAction()
	// Its own busy state, so Cancel and Finish signing in show only for a sign-in (Ruling 69).
	const turningOff = useAction()
	const busy = acting || turningOff.busy
	const error = actionError ?? turningOff.error
	// What the last sign-out said (kept-on-server); a pushed state doesn't carry it.
	const [signOutNotice, setSignOutNotice] = useState<SyncNotice>()
	const [confirmingUpload, setConfirmingUpload] = useState(false)
	const [confirmingTurnOff, setConfirmingTurnOff] = useState(false)
	// A state pushed after an action failed is newer than that failure, so the failure goes (M3).
	const [failedAgainst, setFailedAgainst] = useState(sync)
	if (failedAgainst !== sync) {
		setFailedAgainst(sync)
		setError(undefined)
		turningOff.setError(undefined)
	}

	const settle = (work: () => Promise<SyncState>, after: (state: SyncState) => void) => async (): Promise<void> => {
		setSignOutNotice(undefined)
		const state = await work()
		setSync(state)
		after(state)
	}
	const act = (work: () => Promise<SyncState>, after: (state: SyncState) => void = () => undefined): void => {
		turningOff.setError(undefined)
		run(settle(work, after))
	}

	const signIn = (): void => act(() => zing.signInToSync())
	const signOut = (): void => act(() => zing.signOutOfSync(), (state) => setSignOutNotice(state.signedIn ? undefined : state.notice))
	const notice = signOutNotice ?? (sync.signedIn ? undefined : sync.notice)
	const banner = syncBanner(sync, error)
	const upload = (): void => {
		setConfirmingUpload(false)
		act(() => zing.uploadLocalCopy())
	}
	const turnOff = (): void => {
		setConfirmingTurnOff(false)
		setError(undefined)
		turningOff.run(settle(() => zing.turnOffSync(), (state) => setSignOutNotice(state.signedIn ? undefined : state.notice)))
	}
	const controls = syncControls(sync, acting)
	const turnOffButton = sync.keysHere ? (
		<PillButton variant="outlined" size="small" disabled={busy} onClick={() => setConfirmingTurnOff(true)}>
			Turn off sync on this PC
		</PillButton>
	) : null

	return (
		<div className="vault-sync" role="group" aria-label="Sync">
			<SettingRowShell title="Sync">
				{controls === 'account' ? (
					<div className="vault-actions">
						{sync.on ? <ShowRecoveryKey /> : null}
						<PillButton variant="outlined" size="small" disabled={busy} onClick={signOut}>
							Sign out
						</PillButton>
						{turnOffButton}
					</div>
				) : controls === 'cancel-sign-in' ? (
					<PillButton variant="outlined" size="small" onClick={() => void zing.cancelSyncSignIn()}>
						Cancel
					</PillButton>
				) : (
					<div className="vault-actions">
						<PillButton variant="outlined" size="small" disabled={busy} onClick={signIn}>
							{signInLabel(sync, notice)}
						</PillButton>
						{turnOffButton}
					</div>
				)}
			</SettingRowShell>
			<div className="setting-row-detail" role="status" aria-live="polite">
				{syncDetail(sync, acting)}
			</div>
			{sync.signedIn && !sync.on ? <SyncSetup turnOn={turnOn} onJoined={setSync} /> : null}
			{sync.signedIn && sync.passwordChangedElsewhere ? (
				<InfoBanner kind="info">Your Zing password was changed on another PC. Zing asks for the new one the next time it starts.</InfoBanner>
			) : null}
			{notice ? <InfoBanner kind={notice === 'kept-on-server' ? 'info' : 'warning'}>{SYNC_NOTICES[notice]}</InfoBanner> : null}
			{banner ? (
				<InfoBanner kind="warning">
					{banner.message}
					{banner.offerUpload && !confirmingUpload ? (
						<PillButton variant="outlined" size="small" disabled={busy} onClick={() => setConfirmingUpload(true)}>
							Upload this PC&rsquo;s copy
						</PillButton>
					) : null}
				</InfoBanner>
			) : null}
			{banner?.offerUpload && confirmingUpload ? (
				<div className="vault-confirm" role="alertdialog" aria-label="Upload this PC's copy" aria-describedby="vault-upload-local">
					<InfoBanner kind="info">
						<span id="vault-upload-local">{UPLOAD_LOCAL_CONFIRM}</span>
					</InfoBanner>
					<div className="vault-edit-actions">
						<PillButton variant="ghost" size="small" autoFocus onClick={() => setConfirmingUpload(false)}>
							Cancel
						</PillButton>
						<PillButton size="small" disabled={busy} onClick={upload}>
							Upload
						</PillButton>
					</div>
				</div>
			) : null}
			{sync.keysHere && confirmingTurnOff ? (
				<div className="vault-confirm" role="alertdialog" aria-label="Turn off sync on this PC" aria-describedby="vault-turn-off-sync">
					<InfoBanner kind="info">
						<span id="vault-turn-off-sync">{TURN_OFF_SYNC_CONFIRM}</span>
					</InfoBanner>
					<div className="vault-edit-actions">
						<PillButton variant="ghost" size="small" autoFocus onClick={() => setConfirmingTurnOff(false)}>
							Cancel
						</PillButton>
						<PillButton size="small" disabled={busy} onClick={turnOff}>
							Turn off
						</PillButton>
					</div>
				</div>
			) : null}
		</div>
	)
}

/** The manage page for the password vault (Settings, Browsing): search, show, copy, edit, delete, import and export. */
export function VaultSection() {
	const vault = useVault()
	const { sync, error: syncError, setSync } = useSyncState()
	// The recovery key turning on sync answered; shown until it's confirmed saved, then dropped.
	const [newRecoveryKey, setNewRecoveryKey] = useState<string>()
	// The host says sync is on as soon as the keys are uploaded, before the logins are: the recovery key waits for the whole upload.
	const [turningOn, setTurningOn] = useState(false)
	// A turn-on that failed after its keys were uploaded: sync is on, so the setup form that would have shown it is gone.
	const [turnOnError, setTurnOnError] = useState<string>()
	const [query, setQuery] = useState('')
	const [revealed, setRevealed] = useState<Revealed>()
	const [editing, setEditing] = useState<string>()
	const [error, setError] = useState<string>()
	const [notice, setNotice] = useState<string>()
	const { imported, pickAndImport } = usePasswordCsvImport()
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

	const turnOnSync = async (password: string): Promise<void> => {
		setTurnOnError(undefined)
		setTurningOn(true)
		try {
			setNewRecoveryKey((await zing.turnOnSync(password)).recoveryKey)
		} catch (failure) {
			setTurnOnError(messageOf(failure))
			throw failure
		} finally {
			setTurningOn(false)
		}
	}

	if (sync?.recoveryPending && !turningOn) {
		return (
			<div className="vault" role="group" aria-label="Saved passwords">
				<div className="vault-sync" role="group" aria-label="Sync">
					{turnOnError ? <InfoBanner kind="warning">{turnOnError}</InfoBanner> : null}
					<RecoveryKeyScreen
						recoveryKey={newRecoveryKey}
						onConfirmed={(confirmed) => {
							setNewRecoveryKey(undefined)
							setSync(confirmed)
						}}
					/>
				</div>
			</div>
		)
	}

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

	const importCsv = (): void => void guarded(pickAndImport)

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
			<SettingRowShell title="Saved passwords" detail={loginsDetail(logins.length)}>
				<div className="vault-actions">
					<PillButton variant="outlined" size="small" disabled={busy} onClick={importCsv}>
						Import CSV
					</PillButton>
					<PillButton variant="outlined" size="small" disabled={busy || logins.length === 0} onClick={() => setConfirmingExport(true)}>
						Export CSV
					</PillButton>
				</div>
			</SettingRowShell>

			{sync ? <SyncRow sync={sync} setSync={setSync} turnOn={turnOnSync} /> : syncError ? <InfoBanner kind="warning">{syncError}</InfoBanner> : null}

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

			<PasswordImportResult imported={imported} />

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

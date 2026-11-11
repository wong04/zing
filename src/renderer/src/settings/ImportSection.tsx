import { useEffect, useState } from 'react'
import type { BookmarkImportOutcome, ImportSourceView } from '@shared/ipc'
import { zing } from '../api'
import { InfoBanner, PillButton } from '../components'
import { fileHelp, outcomeLine } from './importText'
import { messageOf, PasswordImportResult, usePasswordCsvImport } from './PasswordImport'
import { SettingRowShell } from './SettingRowShell'
import { useVault } from './useVaultData'

/** Which row ran, so its result sits under it. */
type RowId = string

const FILE_ROW: RowId = 'bookmarks-file'

function sourceDetail(source: ImportSourceView): string {
	if (source.error) return source.error
	const count = source.bookmarks ?? 0
	return `${count} ${count === 1 ? 'bookmark' : 'bookmarks'}`
}

function Outcome({ outcome }: { outcome: BookmarkImportOutcome }) {
	return (
		<div className="vault-import" role="status">
			<InfoBanner kind="info">{outcomeLine(outcome)}</InfoBanner>
			{outcome.skipped.length > 0 ? (
				<details className="import-skipped">
					<summary>Skipped ({outcome.skipped.length})</summary>
					<ul className="vault-skipped" aria-label="Skipped bookmarks">
						{outcome.skipped.map((reason, index) => (
							<li key={index}>{reason}</li>
						))}
					</ul>
				</details>
			) : null}
		</div>
	)
}

/** Settings, Import: bring bookmarks (and passwords) in from other browsers on this PC, or from a file. */
export function ImportSection() {
	const vault = useVault()
	const [sources, setSources] = useState<readonly ImportSourceView[]>()
	const [listError, setListError] = useState<string>()
	const [busy, setBusy] = useState(false)
	const [error, setError] = useState<{ row: RowId; message: string }>()
	const [result, setResult] = useState<{ row: RowId; outcome: BookmarkImportOutcome }>()
	const passwords = usePasswordCsvImport()

	useEffect(() => {
		let current = true
		zing.importSources().then(
			(found) => {
				if (current) setSources(found)
			},
			(failure: unknown) => {
				if (current) setListError(messageOf(failure))
			},
		)
		return () => {
			current = false
		}
	}, [])

	const run = async (row: RowId, work: () => Promise<void>): Promise<void> => {
		setBusy(true)
		setError(undefined)
		setResult(undefined)
		try {
			await work()
		} catch (failure) {
			setError({ row, message: messageOf(failure) })
		} finally {
			setBusy(false)
		}
	}

	const importFrom = (source: ImportSourceView): void =>
		void run(source.id, async () => setResult({ row: source.id, outcome: await zing.importBookmarks(source.id) }))

	const importBookmarksFile = (): void =>
		void run(FILE_ROW, async () => {
			const [file] = await zing.pickFiles(['bookmarks'])
			if (!file) return
			setResult({ row: FILE_ROW, outcome: await zing.importBookmarksFile(file.token) })
		})

	const importPasswordsFile = (): void => void run('passwords-file', passwords.pickAndImport)

	const rowOutcome = (row: RowId) => (
		<>
			{error?.row === row ? <InfoBanner kind="warning">{error.message}</InfoBanner> : null}
			{result?.row === row ? <Outcome outcome={result.outcome} /> : null}
		</>
	)

	const vaultOn = vault?.state.enabled === true

	return (
		<div className="vault" role="group" aria-label="Import">
			<div className="vault-subtitle">On this PC</div>
			{listError ? <InfoBanner kind="warning">{listError}</InfoBanner> : null}
			{sources?.length === 0 ? <div className="vault-none">Zing found no supported browser on this PC.</div> : null}
			{sources?.map((source) => (
				<div key={source.id} className="vault" role="group" aria-label={source.display}>
					<SettingRowShell title={source.display} detail={sourceDetail(source)} warning={source.error !== undefined}>
						<div className="vault-actions">
							<PillButton variant="outlined" size="small" disabled={busy || source.error !== undefined} onClick={() => importFrom(source)}>
								Import bookmarks
							</PillButton>
						</div>
					</SettingRowShell>
					{rowOutcome(source.id)}
				</div>
			))}

			<div className="vault-subtitle">From a file</div>
			<SettingRowShell title="Bookmarks file" detail="An HTML bookmarks export from any browser.">
				<div className="vault-actions">
					<PillButton variant="outlined" size="small" disabled={busy} onClick={importBookmarksFile}>
						Bookmarks file…
					</PillButton>
				</div>
			</SettingRowShell>
			{rowOutcome(FILE_ROW)}
			{vault?.state.error === 'unreadable' ? (
				<InfoBanner kind="warning">Zing couldn&rsquo;t read the saved-passwords file, so the vault is off. The file has not been changed or removed.</InfoBanner>
			) : null}
			{vaultOn ? (
				<>
					<SettingRowShell title="Passwords file" detail="A CSV export from a password manager or browser.">
						<div className="vault-actions">
							<PillButton variant="outlined" size="small" disabled={busy} onClick={importPasswordsFile}>
								Passwords file…
							</PillButton>
						</div>
					</SettingRowShell>
					{error?.row === 'passwords-file' ? <InfoBanner kind="warning">{error.message}</InfoBanner> : null}
					<PasswordImportResult imported={passwords.imported} />
				</>
			) : null}
			<ul className="import-help" aria-label="Exporting a bookmarks file">
				{fileHelp.map((help) => (
					<li key={help.app}>
						<strong>{help.app}</strong> {help.steps}
					</li>
				))}
			</ul>
		</div>
	)
}

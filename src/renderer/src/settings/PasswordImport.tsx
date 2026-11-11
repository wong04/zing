import { useState } from 'react'
import type { VaultImportResult } from '@shared/vault/vault'
import { zing } from '../api'
import { InfoBanner } from '../components'

/** What went wrong, for the person. A refused host call carries the host's own sentence. */
export function messageOf(error: unknown): string {
	if (!(error instanceof Error)) throw error
	// The bridge puts the channel in front ("vault:reveal: ..."); the person doesn't need it.
	return error.message.replace(/^[a-z-]+:[a-z-]+: /, '')
}

/** Picks a password CSV and imports it into the vault. Rejects with what the host refuses with; the caller shows it. */
export function usePasswordCsvImport(): { imported: VaultImportResult | undefined; pickAndImport: () => Promise<void> } {
	const [imported, setImported] = useState<VaultImportResult>()
	const pickAndImport = async (): Promise<void> => {
		setImported(undefined)
		const [file] = await zing.pickFiles(['csv'])
		if (!file) return
		setImported(await zing.importPasswordsCsv(file.token))
	}
	return { imported, pickAndImport }
}

export function PasswordImportResult({ imported }: { imported: VaultImportResult | undefined }) {
	if (!imported) return null
	return (
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
	)
}

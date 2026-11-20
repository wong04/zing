import { useEffect, useState } from 'react'
import { zing } from '../api'
import { PillButton } from '../components'
import { messageOf } from './PasswordImport'
import { setCrashLogKept } from './privacy'
import { SettingRowShell } from './SettingRowShell'

export function CrashLogRowView({ exists, error, onOpen, onClear }: { exists: boolean | undefined; error: string | undefined; onOpen: () => void; onClear: () => void }) {
	if (exists === undefined) return null
	return (
		<>
			<SettingRowShell title="Crash log" detail={exists ? 'When Zing’s renderer or web engine crashed, with the exit code and version. Kept on this PC and never sent.' : 'No crashes logged.'}>
				{exists ? (
					<div className="vault-actions">
						<PillButton variant="outlined" size="small" onClick={onOpen}>
							Open crash log
						</PillButton>
						<PillButton variant="outlined" size="small" onClick={onClear}>
							Clear crash log
						</PillButton>
					</div>
				) : null}
			</SettingRowShell>
			{error ? <div className="setting-row-detail warning">{error}</div> : null}
		</>
	)
}

/** Settings › About: the local crash log, which a tab shows as plain text. */
export function CrashLogRow() {
	const [exists, setExists] = useState<boolean>()
	const [error, setError] = useState<string>()

	useEffect(() => {
		let current = true
		zing.getCrashLog().then(
			(log) => {
				if (current) setExists(log.exists)
			},
			(failure: unknown) => {
				if (current) setError(messageOf(failure))
			},
		)
		return () => {
			current = false
		}
	}, [])

	const open = (): void => {
		setError(undefined)
		zing.openCrashLog().catch((failure: unknown) => setError(messageOf(failure)))
	}
	const clear = (): void => {
		setError(undefined)
		zing.clearCrashLog().then(
			() => {
				setCrashLogKept(false)
				setExists(false)
			},
			(failure: unknown) => setError(messageOf(failure)),
		)
	}
	return <CrashLogRowView exists={exists} error={error} onOpen={open} onClear={clear} />
}

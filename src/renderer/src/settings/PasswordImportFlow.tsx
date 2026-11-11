import { useEffect, useRef, useState } from 'react'
import type { ImportSourceView } from '@shared/ipc'
import { zing } from '../api'
import { InfoBanner, PillButton } from '../components'
import { CONFIRM_VAULT_COPY, passwordImportStep, RECYCLE_COPY, type PasswordImportEvent, type PasswordImportState } from './importText'
import { messageOf, PasswordImportResult, usePasswordCsvImport } from './PasswordImport'

interface PasswordImportFlowProps {
	source: ImportSourceView
	vaultOn: boolean
	/** The flow is over (cancelled, or finished and acknowledged). */
	onClose: () => void
}

const browserName = (source: ImportSourceView): string => source.browser[0]!.toUpperCase() + source.browser.slice(1)

/** Under a source's row: opens its export page, catches the exported CSV from Downloads, imports it and offers to recycle the file. */
export function PasswordImportFlow({ source, vaultOn, onClose }: PasswordImportFlowProps) {
	const [state, setState] = useState<PasswordImportState>(() => passwordImportStep({ step: 'idle' }, { type: 'begin', vaultOn }).state)
	const [file, setFile] = useState<{ token: string; name: string }>()
	const csv = usePasswordCsvImport()
	// The latest step, for handlers that outlive a render (host events, awaited calls).
	const stateRef = useRef(state)

	const dispatch = (event: PasswordImportEvent): void => {
		const next = passwordImportStep(stateRef.current, event)
		stateRef.current = next.state
		setState(next.state)
		if (next.cancelWatch) void zing.cancelPasswordWatch()
	}
	const fail = (failure: unknown): void => dispatch({ type: 'failed', message: messageOf(failure) })

	const waiting = state.step === 'waiting'
	useEffect(() => {
		if (!waiting) return
		const stopFile = zing.onPasswordExport((found) => {
			setFile(found)
			dispatch({ type: 'file-arrived' })
		})
		const stopTimeout = zing.onPasswordWatchTimedOut(() => dispatch({ type: 'timed-out' }))
		zing.watchPasswords(source.id).catch(fail)
		return () => {
			stopFile()
			stopTimeout()
		}
		// eslint-disable-next-line react-hooks/exhaustive-deps -- dispatch and fail read only a ref and setters
	}, [waiting, source.id])

	// Leaving Settings ends the watch.
	useEffect(() => () => void zing.cancelPasswordWatch(), [])

	// Strict mode runs an effect twice; a second import would report everything as merged.
	const imported = useRef<string | undefined>(undefined)
	const importingToken = state.step === 'importing' ? file?.token : undefined
	useEffect(() => {
		if (importingToken === undefined || imported.current === importingToken) return
		imported.current = importingToken
		csv.importToken(importingToken).then(() => {
			dispatch({ type: 'imported' })
			dispatch({ type: 'continue' })
		}, fail)
		// eslint-disable-next-line react-hooks/exhaustive-deps -- one import per chosen file
	}, [importingToken])

	const chooseMyself = async (): Promise<void> => {
		try {
			const [picked] = await zing.pickFiles(['csv'])
			if (!picked) return
			setFile(picked)
			dispatch({ type: 'choose-file' })
		} catch (failure) {
			fail(failure)
		}
	}

	const turnOnVault = async (): Promise<void> => {
		try {
			await zing.setSetting('passwordAutofill', true)
			dispatch({ type: 'confirm-vault' })
		} catch (failure) {
			fail(failure)
		}
	}

	const recycle = async (): Promise<void> => {
		if (!file) return
		try {
			await zing.recycleFile(file.token)
			dispatch({ type: 'recycled' })
		} catch (failure) {
			fail(failure)
		}
	}

	const close = (): void => {
		const { step } = stateRef.current
		if (step === 'confirm-vault' || step === 'waiting' || step === 'timed-out' || step === 'error') dispatch({ type: 'cancel' })
		onClose()
	}

	const cancelRow = (
		<PillButton variant="outlined" size="small" onClick={close}>
			Cancel
		</PillButton>
	)
	const chooseButton = (
		<PillButton variant="outlined" size="small" onClick={() => void chooseMyself()}>
			Choose the file myself…
		</PillButton>
	)

	return (
		<div className="vault-import" role="group" aria-label={`Import ${browserName(source)} passwords`}>
			{state.step === 'confirm-vault' ? (
				<>
					<InfoBanner kind="warning">{CONFIRM_VAULT_COPY}</InfoBanner>
					<div className="vault-actions">
						<PillButton size="small" onClick={() => void turnOnVault()}>
							Turn on and continue
						</PillButton>
						{cancelRow}
					</div>
				</>
			) : null}
			{state.step === 'waiting' ? (
				<>
					<InfoBanner kind="info">
						In {browserName(source)}, click <strong>Export passwords</strong>, confirm, and save. Zing will pick up the file.
					</InfoBanner>
					<div className="vault-actions">
						{chooseButton}
						{cancelRow}
					</div>
				</>
			) : null}
			{state.step === 'timed-out' ? (
				<>
					<InfoBanner kind="warning">Zing didn&rsquo;t see an export arrive in Downloads.</InfoBanner>
					<div className="vault-actions">
						{chooseButton}
						{cancelRow}
					</div>
				</>
			) : null}
			{state.step === 'importing' ? <InfoBanner kind="info">Importing {file?.name ?? 'the file'}…</InfoBanner> : null}
			{state.step === 'offer-recycle' || state.step === 'done' ? <PasswordImportResult imported={csv.imported} /> : null}
			{state.step === 'offer-recycle' ? (
				<>
					<InfoBanner kind="warning">{RECYCLE_COPY}</InfoBanner>
					<div className="vault-actions">
						<PillButton size="small" onClick={() => void recycle()}>
							Move it to the Recycle Bin
						</PillButton>
						<PillButton variant="outlined" size="small" onClick={() => dispatch({ type: 'kept' })}>
							Keep it
						</PillButton>
					</div>
				</>
			) : null}
			{state.step === 'done' ? (
				<div className="vault-actions">
					<PillButton variant="outlined" size="small" onClick={close}>
						Done
					</PillButton>
				</div>
			) : null}
			{state.step === 'error' ? (
				<>
					<InfoBanner kind="warning">{state.message}</InfoBanner>
					<div className="vault-actions">
						<PillButton variant="outlined" size="small" onClick={close}>
							Close
						</PillButton>
					</div>
				</>
			) : null}
		</div>
	)
}

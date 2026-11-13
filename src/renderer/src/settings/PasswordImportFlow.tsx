import { useEffect, useRef, useState } from 'react'
import type { ImportSourceView } from '@shared/ipc'
import { zing } from '../api'
import { InfoBanner, PillButton } from '../components'
import { CONFIRM_VAULT_COPY, EXPORT_BUTTON, EXPORT_COPY_AFTER, exportMenuPath, passwordImportAccepts, passwordImportFailureApplies, passwordImportStep, RECYCLE_COPY, type PasswordImportEvent, type PasswordImportState } from './importText'
import { messageOf, PasswordImportResult, usePasswordCsvImport } from './PasswordImport'

interface PasswordImportFlowProps {
	source: ImportSourceView
	vaultOn: boolean
	/** The flow is over (cancelled, or finished and acknowledged). */
	onClose: () => void
}

/** A refused cancel can't be shown (the flow is closing), so it is thrown out of the call stack for the app's error reporting to record. */
function cancelWatch(): void {
	zing.cancelPasswordWatch().catch((failure: unknown) => {
		queueMicrotask(() => {
			throw failure
		})
	})
}

const browserName = (source: ImportSourceView): string => source.browser[0]!.toUpperCase() + source.browser.slice(1)

/** Under a source's row: opens its export page, catches the exported CSV from Downloads, imports it and offers to recycle the file. */
export function PasswordImportFlow({ source, vaultOn, onClose }: PasswordImportFlowProps) {
	const [state, setState] = useState<PasswordImportState>(() => passwordImportStep({ step: 'idle' }, { type: 'begin', vaultOn }).state)
	const [file, setFile] = useState<{ token: string; name: string }>()
	const csv = usePasswordCsvImport()
	// The latest step, for handlers that outlive a render (host events, awaited calls).
	const stateRef = useRef(state)
	// A click that is still awaiting the host; a second click must not start another.
	const busy = useRef(false)
	const [pending, setPending] = useState(false)

	const dispatch = (event: PasswordImportEvent): void => {
		const next = passwordImportStep(stateRef.current, event)
		stateRef.current = next.state
		setState(next.state)
		if (next.cancelWatch) cancelWatch()
	}
	/** Dispatches unless the step has moved on since the caller started waiting. */
	const dispatchIfAble = (event: PasswordImportEvent): boolean => {
		if (!passwordImportAccepts(stateRef.current, event)) return false
		dispatch(event)
		return true
	}
	const failDuring = (during: PasswordImportState['step']) => (failure: unknown): void => {
		if (passwordImportFailureApplies(stateRef.current, during)) dispatch({ type: 'failed', message: messageOf(failure) })
	}

	const waiting = state.step === 'waiting'
	useEffect(() => {
		if (!waiting) return
		const stopFile = zing.onPasswordExport((found) => {
			if (stateRef.current.step !== 'waiting') return
			setFile(found)
			dispatch({ type: 'file-arrived' })
		})
		const stopTimeout = zing.onPasswordWatchTimedOut(() => {
			if (stateRef.current.step === 'waiting') dispatch({ type: 'timed-out' })
		})
		zing.watchPasswords(source.id).catch(failDuring('waiting'))
		return () => {
			stopFile()
			stopTimeout()
		}
		// eslint-disable-next-line react-hooks/exhaustive-deps -- dispatch and fail read only a ref and setters
	}, [waiting, source.id])

	// Leaving Settings ends the watch.
	useEffect(() => cancelWatch, [])

	// Strict mode runs an effect twice; a second import would report everything as merged.
	const imported = useRef<string | undefined>(undefined)
	const importingToken = state.step === 'importing' ? file?.token : undefined
	useEffect(() => {
		if (importingToken === undefined || imported.current === importingToken) return
		imported.current = importingToken
		csv
			.importToken(importingToken)
			.then(() => {
				if (!dispatchIfAble({ type: 'imported' })) return
				dispatch({ type: 'continue' })
			})
			.catch(failDuring('importing'))
		// eslint-disable-next-line react-hooks/exhaustive-deps -- one import per chosen file
	}, [importingToken])

	const chooseMyself = async (): Promise<void> => {
		const fail = failDuring(stateRef.current.step)
		try {
			const [picked] = await zing.pickFiles(['csv'])
			// The export may have landed while the picker was open; then that file stands.
			if (!picked || !passwordImportAccepts(stateRef.current, { type: 'choose-file' })) return
			setFile(picked)
			dispatch({ type: 'choose-file' })
		} catch (failure) {
			fail(failure)
		}
	}

	const turnOnVault = async (): Promise<void> => {
		if (busy.current) return
		busy.current = true
		setPending(true)
		try {
			await zing.setSetting('passwordAutofill', true)
			dispatchIfAble({ type: 'confirm-vault' })
		} catch (failure) {
			failDuring('confirm-vault')(failure)
		} finally {
			busy.current = false
			setPending(false)
		}
	}

	const recycle = async (): Promise<void> => {
		if (!file || busy.current) return
		busy.current = true
		setPending(true)
		try {
			await zing.recycleFile(file.token)
			dispatchIfAble({ type: 'recycled' })
		} catch (failure) {
			failDuring('offer-recycle')(failure)
		} finally {
			busy.current = false
			setPending(false)
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
						<PillButton size="small" disabled={pending} onClick={() => void turnOnVault()}>
							Turn on and continue
						</PillButton>
						{cancelRow}
					</div>
				</>
			) : null}
			{state.step === 'waiting' ? (
				<>
					<InfoBanner kind="info">
						In {browserName(source)}, click <strong>{EXPORT_BUTTON}</strong>
						{EXPORT_COPY_AFTER}
					</InfoBanner>
					{exportMenuPath[source.browser] ? (
						<div className="import-menu-path">
							If {browserName(source)} shows a new tab instead, open {exportMenuPath[source.browser]}.
						</div>
					) : null}
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
						<PillButton size="small" disabled={pending} onClick={() => void recycle()}>
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

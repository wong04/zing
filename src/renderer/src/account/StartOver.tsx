import { useEffect, useState } from 'react'
import type { StartOverPlan } from '@shared/shell/shellState'
import { zing } from '../api'
import { InfoBanner, PillButton, PillField } from '../components'
import { messageOf, useAction } from '../settings/SyncSetup'

/** What the person types before anything is deleted. */
export const START_OVER_CONFIRM = 'START OVER'

/**
 * "Forgot your password? Start over." For someone with no Zing password and no recovery key: says what goes, offers to delete the
 * sync account too when this PC syncs (after a Google sign-in that proves it's theirs), and deletes nothing until the confirmation is
 * typed. Afterwards the gate offers a new account.
 */
export function StartOver({ onBack, overSettings = false }: { onBack: () => void; overSettings?: boolean }) {
	const [plan, setPlan] = useState<StartOverPlan>()
	const [planError, setPlanError] = useState<string>()
	const [deleteSync, setDeleteSync] = useState(false)
	const [typed, setTyped] = useState('')
	const { busy, error, run } = useAction()

	useEffect(() => {
		let current = true
		zing.startOverPlan().then(
			(answer) => {
				if (current) setPlan(answer)
			},
			(failure: unknown) => {
				if (current) setPlanError(messageOf(failure))
			},
		)
		return () => {
			current = false
		}
	}, [])

	const confirmed = typed === START_OVER_CONFIRM
	const start = (): void =>
		run(async () => {
			const outcome = deleteSync ? await zing.startOverDeletingSync() : await zing.startOver()
			// The Google sign-in was cancelled: nothing was deleted.
			if (outcome === null) return
			if (outcome.status === 'rejected') throw new Error(outcome.message)
		})

	return (
		<div className={overSettings ? 'auth auth-over-settings' : 'auth'}>
			<div className="auth-drag" />
			<div className="auth-card card-surface" role="form" aria-label="Start over">
				<div className="auth-header">
					<div className="auth-mark" aria-hidden="true">
						Z
					</div>
					<h1>Start over</h1>
					<p>If you&rsquo;ve forgotten your Zing password and don&rsquo;t have your recovery key, you can start again with a new account.</p>
				</div>
				<InfoBanner kind="warning">
					This deletes your Zing account on this PC and every password saved in Zing on this PC. They can&rsquo;t be brought back. Your
					settings, bookmarks and history stay.
				</InfoBanner>
				{planError ? <InfoBanner kind="warning">{planError}</InfoBanner> : null}
				{plan?.syncs ? (
					<label className="auth-start-over-sync">
						<input type="checkbox" checked={deleteSync} disabled={busy} onChange={(e) => setDeleteSync(e.target.checked)} />
						<span>
							Also delete my sync account and its encrypted passwords on the sync server. Zing asks you to sign in with Google first, to
							prove the account is yours. Your other PCs stop syncing. If you leave this unticked, the server copy stays, still locked
							with the password you forgot.
						</span>
					</label>
				) : null}
				<PillField label={`Type ${START_OVER_CONFIRM} to confirm`} value={typed} onChange={setTyped} autoComplete="off" />
				{error ? <InfoBanner kind="warning">{error}</InfoBanner> : null}
				<div className="auth-links">
					<PillButton variant="ghost" size="small" disabled={busy} onClick={onBack}>
						Back
					</PillButton>
					<PillButton size="small" disabled={busy || !confirmed || !plan} onClick={start}>
						{deleteSync ? 'Sign in with Google and start over' : 'Start over'}
					</PillButton>
				</div>
			</div>
		</div>
	)
}

import { useEffect, useRef, useState } from 'react'
import { declinedNotice, dohNotice } from '@shared/unblock/dohNotice'
import { attemptSummary, collapseAttempts, pinnedFilteredNote, planOpening, strategyLabel, withAttempt, type Opening } from '@shared/unblock/openLinkPlan'
import type { RelayAttempt, RelayOpenResult } from '@shared/unblock/relayOpen'
import { zing } from '../../api'
import { InfoBanner, PillButton, PillField } from '../../components'
import { DOH_LABELS } from '../../settings/rows'
import { useSettings } from '../../settings/useSettings'
import { usePanelInput } from '../panelInput'
import { ToolPage } from '../parts'
import './web.css'

/** The words of the first-use dialog, as the design gives them. */
export const consentText =
	"This asks an outside service to fetch the page for you, so that service sees the address, and for a reader relay the page too. Files the page you open asks for may go the same way. Zing doesn't send it your cookies or sign-ins. If this network belongs to a school or employer, getting around its filter may break their rules. That's your call."

const warnedText = "This address looks unsafe, so nothing was sent to it or to any outside service. A warning is open in a new tab, where you can choose Visit anyway."

/** An http address that needed a relay, and neither it nor its https form could be opened that way. */
class UnrelayableAddressError extends Error {
	override name = 'UnrelayableAddressError'
	constructor(address: string, cause?: unknown) {
		const why = cause instanceof Error ? ` The https form didn't open either: ${cause.message}` : ''
		super(`${address} can't be relayed: a relayed page needs an https address, and this one only worked over http.${why}`)
	}
}

type Phase = 'idle' | 'running' | 'asking'

function describeOpening(opening: Opening, result: RelayOpenResult): string {
	const method = result.strategy === undefined ? 'A method' : strategyLabel(result.strategy)
	if (opening.kind === 'ordinary') return `Opened in a new tab. ${method} worked, so the page loads as usual.`
	const reason = 'Opened in a new tab through the relay, because an ordinary tab would meet the same block.'
	return `${reason} ${method} worked. Pages that need a sign-in won't work this way, and web apps usually don't either.`
}

function Attempts({ attempts, label = 'Attempts' }: { attempts: readonly RelayAttempt[]; label?: string }) {
	if (attempts.length === 0) return null
	const pinnedMissed = attempts.some((a) => a.strategy === 'dohPinned' && a.status === 'failed')
	return (
		<>
			<ol className="attempt-list" aria-label={label}>
				{attempts.map((attempt) => (
					<li key={attempt.strategy} className={`attempt ${attempt.status}`} data-testid="attempt" data-strategy={attempt.strategy} data-status={attempt.status}>
						<span className="attempt-name">{strategyLabel(attempt.strategy)}</span>
						<span className="attempt-result">{attemptSummary(attempt)}</span>
					</li>
				))}
			</ol>
			{pinnedMissed ? (
				<p className="diagnose-detail" data-testid="pinned-note">
					{pinnedFilteredNote}
				</p>
			) : null}
		</>
	)
}

/** Asks before the first time an outside service is used. Escape and the first button decline; focus lands on the dialog when it opens. */
function ConsentDialog({ onAllow, onDecline }: { onAllow: () => void; onDecline: () => void }) {
	const declineButton = useRef<HTMLButtonElement>(null)
	useEffect(() => declineButton.current?.focus(), [])
	// On the document, not the dialog: a click on its text moves focus to the body, and Escape must still decline.
	useEffect(() => {
		const onKey = (e: KeyboardEvent): void => {
			if (e.key !== 'Escape') return
			e.stopPropagation()
			onDecline()
		}
		document.addEventListener('keydown', onKey, true)
		return () => document.removeEventListener('keydown', onKey, true)
	}, [onDecline])
	return (
		<div
			className="consent-dialog"
			role="alertdialog"
			aria-modal="true"
			aria-labelledby="consent-title"
			aria-describedby="consent-text"
			data-testid="consent-dialog"
		>
			<strong id="consent-title">Ask an outside service?</strong>
			<p id="consent-text">{consentText}</p>
			<div className="consent-actions">
				<button ref={declineButton} type="button" className="pill-button outlined regular" onClick={onDecline}>
					Don't allow
				</button>
				<PillButton onClick={onAllow}>Allow for this session</PillButton>
			</div>
		</div>
	)
}

/** Open a Link (X1): tries each way of reaching a page in order, shows which worked, and opens it in a tab. */
export function OpenLinkTool() {
	const handoff = usePanelInput()
	const provider = useSettings()?.settings.dohProvider
	const [address, setAddress] = useState(handoff?.value ?? '')
	const [seenHandoff, setSeenHandoff] = useState(handoff?.serial)
	const [phase, setPhase] = useState<Phase>('idle')
	const [attempts, setAttempts] = useState<readonly RelayAttempt[]>([])
	// How the http address went, kept in view while its https form is tried.
	const [earlier, setEarlier] = useState<readonly RelayAttempt[]>([])
	const [outcome, setOutcome] = useState<string | null>(null)
	const [problem, setProblem] = useState<string | null>(null)
	const [stopped, setStopped] = useState<string | null>(null)
	// What the run the dialog is about was started for.
	const [asked, setAsked] = useState('')

	// An address handed over replaces the field; it never starts anything. While a run or its question is open it waits,
	// so "Allow" and the result are always about the address the field showed when Open was clicked.
	if (handoff && handoff.serial !== seenHandoff && phase === 'idle') {
		setSeenHandoff(handoff.serial)
		setAddress(handoff.value)
	}

	// A later result for a panel that was closed, or for a run a newer one replaced, must show nothing and open nothing.
	const mounted = useRef(true)
	const latest = useRef(0)
	const currentRun = useRef('')
	useEffect(() => {
		mounted.current = true
		return () => {
			mounted.current = false
		}
	}, [])
	useEffect(
		() =>
			zing.onRelayAttempt((event) => {
				if (event.runId !== currentRun.current) return
				setAttempts((rows) => withAttempt(rows, event))
			}),
		[],
	)

	// Closing the dialog puts the keyboard back in the address field, which was out of reach while it was open.
	const form = useRef<HTMLDivElement>(null)
	const wasAsking = useRef(false)
	useEffect(() => {
		if (phase === 'idle' && wasAsking.current) form.current?.querySelector('input')?.focus()
		wasAsking.current = phase === 'asking'
	}, [phase])

	const alive = (mine: number): boolean => mounted.current && mine === latest.current

	const runOnce = async (input: string, mine: number): Promise<RelayOpenResult | null> => {
		const runId = crypto.randomUUID()
		currentRun.current = runId
		setAttempts([])
		const result = await zing.openLink(input, runId)
		if (!alive(mine)) return null
		setAttempts(collapseAttempts(result.attempts))
		return result
	}

	const openTab = async (opening: Opening): Promise<void> => {
		if (opening.kind === 'ordinary') return zing.openLinkInNewTab(opening.url)
		if (opening.kind === 'relayed') return zing.openRelayedTab(opening.url)
		throw new Error('An opening that needs another try was not tried again')
	}

	const secondTry = async (first: RelayOpenResult, httpsAddress: string, mine: number): Promise<RelayOpenResult | null> => {
		const httpAddress = first.address
		setEarlier(collapseAttempts(first.attempts))
		setOutcome("That address is http, which a relayed page can't use. Trying the https form of it once.")
		let retried: RelayOpenResult | null
		try {
			retried = await runOnce(httpsAddress, mine)
		} catch (error) {
			throw new UnrelayableAddressError(httpAddress, error)
		}
		if (retried?.outcome === 'needsConsent') throw new UnrelayableAddressError(httpAddress)
		return retried
	}

	const start = async (input: string): Promise<void> => {
		const mine = ++latest.current
		setPhase('running')
		setOutcome(null)
		setProblem(null)
		setStopped(null)
		setEarlier([])
		try {
			let result = await runOnce(input, mine)
			if (!result) return
			if (result.outcome === 'warned') {
				setOutcome(warnedText)
				setPhase('idle')
				return
			}
			if (result.outcome === 'needsConsent') {
				setAsked(input)
				setPhase('asking')
				return
			}
			let opening = planOpening(result)
			if (opening.kind === 'retryHttps') {
				const retried = await secondTry(result, opening.url, mine)
				if (!retried) return
				if (retried.outcome === 'warned') {
					setOutcome(warnedText)
					setPhase('idle')
					return
				}
				result = retried
				opening = planOpening(retried)
			}
			await openTab(opening)
			setOutcome(describeOpening(opening, result))
			setPhase('idle')
		} catch (error) {
			if (!alive(mine)) return
			setOutcome(null)
			setProblem(error instanceof Error ? error.message : String(error))
			setPhase('idle')
		}
	}

	const allow = async (): Promise<void> => {
		try {
			await zing.allowRelayThirdParties()
		} catch (error) {
			if (!mounted.current) return
			setProblem(error instanceof Error ? error.message : String(error))
			setPhase('idle')
			return
		}
		if (mounted.current) await start(asked)
	}

	const decline = (): void => {
		setPhase('idle')
		setStopped(declinedNotice(providerName()))
	}

	const providerName = (): string => {
		if (provider === undefined) throw new Error('The encrypted DNS provider setting is not loaded')
		return DOH_LABELS[provider]
	}

	const running = phase !== 'idle'
	const trying = phase === 'running' ? attempts.find((a) => a.status === 'trying') : undefined

	return (
		<ToolPage subtitle="Tries ways of reaching a page that your network doesn't let through, and says which one worked.">
			{provider === undefined ? null : (
				<p className="tool-subtitle" data-testid="doh-notice">
					{dohNotice(DOH_LABELS[provider])}
				</p>
			)}
			<div className="diagnose-form" ref={form} inert={phase === 'asking'}>
				<PillField label="Address" value={address} onChange={setAddress} onSubmit={() => void start(address)} placeholder="example.com/page" disabled={running} />
				<PillButton disabled={running || address.trim() === ''} onClick={() => void start(address)}>
					Open
				</PillButton>
			</div>
			{phase === 'asking' ? <ConsentDialog onAllow={() => void allow()} onDecline={decline} /> : null}
			{trying === undefined ? null : (
				<p className="tool-subtitle" data-testid="trying">
					Trying {strategyLabel(trying.strategy)}…
				</p>
			)}
			{earlier.length === 0 ? null : (
				<>
					<p className="tool-subtitle">First try, over http</p>
					<Attempts attempts={earlier} label="Attempts over http" />
					<p className="tool-subtitle">Second try, over https</p>
				</>
			)}
			<Attempts attempts={attempts} />
			{stopped === null ? null : (
				<div data-testid="open-link-stopped">
					<InfoBanner kind="info">{stopped}</InfoBanner>
				</div>
			)}
			{outcome === null ? null : (
				<div data-testid="open-link-outcome">
					<InfoBanner kind="info">{outcome}</InfoBanner>
				</div>
			)}
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
		</ToolPage>
	)
}

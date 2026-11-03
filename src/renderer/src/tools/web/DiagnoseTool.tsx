import { useRef, useState } from 'react'
import { panelId, section, Section } from '@shared/toolbox/panelContent'
import { blockDetail, blockTitle, recommendedStrategies, type DiagnosticReport } from '@shared/unblock/blockDiagnostics'
import { zing } from '../../api'
import { InfoBanner, PillButton, PillField } from '../../components'
import { PANELS_FULL_MESSAGE } from '@shared/shell/shellState'
import { usePanelInput } from '../panelInput'
import { ResultRow, TextArea, ToolPage } from '../parts'
import './web.css'

function reportText(report: DiagnosticReport): string {
	return [`${report.host}: ${blockTitle(report.kind)}`, ...report.steps].join('\n')
}

/** Diagnose (X3): which layer a site is blocked at, found by comparing DNS answers and trying TCP and TLS. */
export function DiagnoseTool() {
	const handoff = usePanelInput()
	const [site, setSite] = useState(handoff?.value ?? '')
	const [seenHandoff, setSeenHandoff] = useState(handoff?.serial)
	// An address handed over (the failed-load offer) replaces the field; it never starts a diagnosis.
	if (handoff && handoff.serial !== seenHandoff) {
		setSeenHandoff(handoff.serial)
		setSite(handoff.value)
	}
	const [running, setRunning] = useState(false)
	const [report, setReport] = useState<DiagnosticReport | null>(null)
	// What the report is for, as it was typed: the address Open a Link starts from, with its path.
	const [diagnosed, setDiagnosed] = useState('')
	const [problem, setProblem] = useState<string | null>(null)
	// A slower earlier run must not replace the report of a later one.
	const latest = useRef(0)

	const run = async (): Promise<void> => {
		const mine = ++latest.current
		setRunning(true)
		setProblem(null)
		try {
			const result = await zing.diagnose(site)
			if (mine === latest.current) {
				setReport(result)
				setDiagnosed(site)
			}
		} catch (error) {
			if (mine !== latest.current) return
			setReport(null)
			setProblem(error instanceof Error ? error.message : String(error))
		} finally {
			if (mine === latest.current) setRunning(false)
		}
	}

	const openMethod = async (): Promise<void> => {
		// Open a Link starts with the address filled in; it runs when it is clicked there.
		const outcome = await zing.openPanelWithInput(panelId(section(Section.unblock)), diagnosed)
		if (outcome.status === 'full') setProblem(PANELS_FULL_MESSAGE)
	}

	const strategies = report === null ? [] : recommendedStrategies(report.kind)
	const [first] = strategies

	return (
		<ToolPage subtitle="Finds where a site is being blocked: your network's DNS, the connection, or the encryption handshake.">
			<div className="diagnose-form">
				<PillField label="Site" value={site} onChange={setSite} onSubmit={() => void run()} placeholder="example.com" />
				<PillButton disabled={running || site.trim() === ''} onClick={() => void run()}>
					Diagnose
				</PillButton>
			</div>
			{running ? <p className="tool-subtitle">Checking your network's DNS, then the connection and the handshake…</p> : null}
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
			{report === null ? null : (
				<>
					<div className="diagnose-verdict" data-testid="diagnose-verdict">
						{blockTitle(report.kind)}
					</div>
					<p className="diagnose-detail" data-testid="diagnose-detail">
						{blockDetail(report.kind)}
					</p>
					{report.kind === 'notBlocked' && report.publicDNS.length === 0 ? (
						<p className="diagnose-detail" data-testid="diagnose-caveat">
							Encrypted DNS couldn't be checked on this network, so this rests on the site's certificate alone.
						</p>
					) : null}
					{report.kind === 'notBlocked' ? null : (
						<>
							<p className="diagnose-detail">
								Worth trying: <span data-testid="diagnose-recommended">{strategies.join(', ')}</span>
							</p>
							{first === undefined ? null : (
								<div>
									<PillButton variant="outlined" size="small" onClick={() => void openMethod()}>
										Open {first} in Open a Link
									</PillButton>
								</div>
							)}
						</>
					)}
					<div className="tool-rows">
						<ResultRow label="Network DNS" value={report.systemDNS.join(', ')} />
						<ResultRow label="Public DNS" value={report.publicDNS.join(', ')} />
					</div>
					<TextArea label="Steps" value={reportText(report)} readOnly rows={Math.min(10, report.steps.length + 1)} />
				</>
			)}
		</ToolPage>
	)
}

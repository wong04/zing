import type { AiConfirm as AiConfirmState } from '@shared/ipc'
import { zing } from '../api'
import { PillButton } from '../components'
import { useAccentTheme } from '../settings/useAccentTheme'
import { useAiTabs } from './useAiTabs'
import { useReportedSize } from './useReportedSize'
import './ai.css'

const reportStrip = (width: number, height: number): Promise<void> => zing.setAiStripSize(width, height)
const reportConfirm = (width: number, height: number): Promise<void> => zing.setAiConfirmSize(width, height)

/** Root of the #ai-strip view, over the page of a shared tab while it is in front. */
export function AiStrip() {
	useAccentTheme()
	const state = useAiTabs()
	const tab = state.activeShared ?? null
	const root = useReportedSize(reportStrip, tab !== null)
	return (
		<div ref={root} className="ai-bar-frame">
			{tab === null ? null : (
				<div className="ai-bar ai-strip" role="status">
					<span className="ai-dot" aria-hidden="true" />
					<span className="ai-strip-text">AI is using this tab</span>
					<PillButton variant="outlined" size="small" onClick={() => void zing.stopAi(tab)}>
						Stop
					</PillButton>
					{state.shared.length > 1 ? (
						<PillButton variant="ghost" size="small" onClick={() => void zing.stopAllAi()}>
							Stop all
						</PillButton>
					) : null}
				</div>
			)}
		</div>
	)
}

/** The question itself. The description comes from the page (a button's name, a field's label): React draws it as text, never markup. */
export function AiConfirmBar({ confirm }: { confirm: AiConfirmState }) {
	const answer = (reply: 'allow' | 'deny' | 'always'): void => void zing.answerAi(confirm.tab, reply)
	return (
		<div className="ai-bar ai-confirm" role="alertdialog" aria-label="AI wants your OK">
			<p className="ai-confirm-text">AI wants to {confirm.description}</p>
			<div className="ai-confirm-actions">
				<PillButton size="small" onClick={() => answer('allow')}>
					Allow
				</PillButton>
				<PillButton variant="outlined" size="small" onClick={() => answer('deny')}>
					Deny
				</PillButton>
				{confirm.canAlwaysAllow ? (
					<PillButton variant="ghost" size="small" onClick={() => answer('always')}>
						Always allow on this site
					</PillButton>
				) : null}
			</div>
		</div>
	)
}

/** Root of the #ai-confirm view, under the strip of the tab in front while its confirm bar is up. */
export function AiConfirm() {
	useAccentTheme()
	const { confirm } = useAiTabs()
	const root = useReportedSize(reportConfirm, confirm !== undefined)
	return (
		<div ref={root} className="ai-bar-frame">
			{confirm ? <AiConfirmBar confirm={confirm} /> : null}
		</div>
	)
}

import { useEffect, useState } from 'react'
import type { PeekState } from '@shared/browser/peek'
import { CloseIcon, ExpandIcon } from '../chrome/icons'
import { followHostState } from '../hostState'
import { zing } from '../api'
import { useAccentTheme } from '../settings/useAccentTheme'
import './peek.css'

/**
 * A link preview's two buttons, in the margin at the preview's top right. The border and dim around the preview are
 * the native scrim's; this view draws only the buttons and the live region that announces the preview. The region
 * stays mounted while no preview is open, so filling it is a change Narrator reads out.
 */
export function PeekButtons({ peek }: { peek: PeekState | null }) {
	return (
		<div className={peek?.highContrast ? 'peek high-contrast' : 'peek'}>
			{peek && (
				<>
					<button type="button" className="peek-button" aria-label="Close preview" title="Close preview (Esc)" onClick={() => void zing.runCommand('preview.close')}>
						<CloseIcon />
					</button>
					<button
						type="button"
						className="peek-button"
						aria-label="Open as tab"
						title="Open as tab (Ctrl+O)"
						disabled={!peek.canExpand}
						onClick={() => void zing.runCommand('preview.expand')}
					>
						<ExpandIcon />
					</button>
				</>
			)}
			<span className="peek-live" aria-live="polite">
				{peek && `Preview of ${peek.host}. Press Escape to close.`}
			</span>
		</div>
	)
}

/** Root of the #peek view: shown by the host only while a preview is open. */
export function PeekControls() {
	useAccentTheme()
	const [peek, setPeek] = useState<PeekState | null>(null)
	useEffect(
		() =>
			followHostState(zing.onPeek, zing.getPeek, (state) => {
				// The bridge leaves out a null value, so a closed preview can arrive as undefined.
				setPeek(state ?? null)
			}),
		[],
	)
	return <PeekButtons peek={peek} />
}

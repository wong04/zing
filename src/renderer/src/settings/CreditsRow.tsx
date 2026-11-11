import { useState } from 'react'
import { zing } from '../api'
import { PillButton } from '../components'
import { SettingRowShell } from './SettingRowShell'

export type CreditsState = { readonly kind: 'closed' } | { readonly kind: 'loading' } | { readonly kind: 'shown'; readonly text: string } | { readonly kind: 'failed'; readonly message: string }

export function CreditsRowView({ state, onToggle }: { state: CreditsState; onToggle: () => void }) {
	const open = state.kind !== 'closed'
	return (
		<>
			<SettingRowShell title="Credits" detail="The open source software and licences Zing includes.">
				<PillButton variant="outlined" size="small" onClick={onToggle}>
					{open ? 'Hide' : 'Show'}
				</PillButton>
			</SettingRowShell>
			{state.kind === 'shown' ? (
				<pre className="credits-text" role="region" aria-label="Credits" tabIndex={0}>
					{state.text}
				</pre>
			) : null}
			{state.kind === 'failed' ? <div className="setting-row-detail warning">Couldn't load the credits: {state.message}</div> : null}
		</>
	)
}

export function CreditsRow() {
	const [state, setState] = useState<CreditsState>({ kind: 'closed' })
	const toggle = (): void => {
		if (state.kind !== 'closed') return setState({ kind: 'closed' })
		setState({ kind: 'loading' })
		zing.getCredits().then(
			(text) => setState({ kind: 'shown', text }),
			(error: unknown) => setState({ kind: 'failed', message: error instanceof Error ? error.message : String(error) }),
		)
	}
	return <CreditsRowView state={state} onToggle={toggle} />
}

import { Component, type ReactNode } from 'react'
import { InfoBanner } from '../components'

interface Props {
	readonly children: ReactNode
}

interface State {
	readonly error: Error | null
}

/** One tool throwing while it renders must not take the sidebar, the other panels and the sheets down with it. */
export class PanelErrorBoundary extends Component<Props, State> {
	override state: State = { error: null }

	static getDerivedStateFromError(error: unknown): State {
		return { error: error instanceof Error ? error : new Error(String(error)) }
	}

	override render(): ReactNode {
		const { error } = this.state
		if (error === null) return this.props.children
		return (
			<div className="panel-failed">
				<InfoBanner kind="warning">This tool stopped working: {error.message}</InfoBanner>
			</div>
		)
	}
}

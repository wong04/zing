import { CopyButton } from '../parts'
import './dev.css'

/** A read-only block of code under a heading, e.g. a decoded JWT header. */
export function CodeBlock({ label, text }: { readonly label: string; readonly text: string }) {
	return (
		<div className="tool-field" role="group" aria-label={label}>
			<div className="tool-field-head">
				<span className="tool-label">{label}</span>
				<CopyButton value={text} label={label} />
			</div>
			<pre className="dev-pre">{text}</pre>
		</div>
	)
}

/** What to tell the person when a task failed with something unexpected. */
export function describeFailure(error: unknown): string {
	return error instanceof Error ? error.message : String(error)
}

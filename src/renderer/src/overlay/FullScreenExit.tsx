import { CloseIcon } from '../chrome/icons'
import { zing } from '../api'
import { useAccentTheme } from '../settings/useAccentTheme'
import './fullscreen.css'

/** The way out of a page's fullscreen that a person can see: the page leaves it as Esc would make it. */
export function FullScreenExitButton() {
	return (
		<button type="button" className="fullscreen-exit-button" aria-label="Exit full screen" title="Exit full screen (Esc)" onClick={() => void zing.exitFullScreen()}>
			<CloseIcon />
		</button>
	)
}

/** Root of the #fullscreen-exit view: the host shows it at the top of a fullscreen page while the cursor is at the top edge. */
export function FullScreenExit() {
	useAccentTheme()
	return (
		<div className="fullscreen-exit">
			<FullScreenExitButton />
		</div>
	)
}

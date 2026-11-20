import { zing } from '../api'

/** The host records the offer as asked when it shows, so either button, or none, ends it for good. */
export function DefaultBrowserOffer() {
	const makeDefault = async (): Promise<void> => {
		await zing.openDefaultAppsSettings()
		await zing.dismissOffer()
	}
	return (
		<div className="notice offer" role="status" data-testid="default-browser-offer">
			<div className="offer-text">
				<strong>Make Zing your default browser?</strong>
			</div>
			<div className="offer-actions">
				<button type="button" className="pill-button filled small" onClick={() => void makeDefault()}>
					Make default
				</button>
				<button type="button" className="pill-button outlined small" onClick={() => void zing.dismissDefaultBrowserOffer()}>
					Not now
				</button>
			</div>
		</div>
	)
}

import { zing } from '../api'

/** Make default only opens Windows' Default apps page, so it isn't a dismissal: if Zing still isn't the default, the offer comes back next launch. */
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
					Don't ask again
				</button>
			</div>
		</div>
	)
}

import { useEffect, useState } from 'react'
import { zing } from '../api'
import { followHostState } from '../hostState'
import { setSiteGrantsKept } from './privacy'

/** The sites remembered as allowed to show notifications, as the host has them; the privacy banner follows. */
export function useSiteGrants(): readonly string[] {
	const [sites, setSites] = useState<readonly string[]>([])
	useEffect(
		() =>
			followHostState(zing.onSiteGrants, zing.getSiteGrants, (state) => {
				setSiteGrantsKept(state.notifications.length > 0)
				setSites(state.notifications)
			}),
		[],
	)
	return sites
}

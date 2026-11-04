import './library.css'
import type { TabBarPosition } from '@shared/layout/chrome'
import { FavouritesBar } from './FavouritesBar'
import { LibraryPanel } from './LibraryPanel'
import { useLibraryState } from './useLibraryState'

/** The Favourites bar under the toolbar, and the side panel docked beside the page. */
export function LibraryChrome({ position }: { readonly position: TabBarPosition }) {
	const state = useLibraryState()
	if (!state) return null
	return (
		<>
			{state.favouritesBar ? <FavouritesBar library={state.library} /> : null}
			{state.panel ? <LibraryPanel library={state.library} panel={state.panel} favouritesBar={state.favouritesBar} offset={state.offset} position={position} /> : null}
		</>
	)
}

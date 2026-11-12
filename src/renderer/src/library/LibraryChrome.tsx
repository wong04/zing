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
			{state.favouritesBar ? <FavouritesBar library={state.library} icons={state.icons} /> : null}
			{state.panel ? <LibraryPanel library={state.library} icons={state.icons} panel={state.panel} favouritesBar={state.favouritesBar} offset={state.offset} position={position} /> : null}
		</>
	)
}

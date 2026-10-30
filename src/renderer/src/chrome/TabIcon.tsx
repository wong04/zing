import type { ReactNode } from 'react'
import type { TabView } from '@shared/ipc'
import { monogram } from './display'

/** The tab's memory-only favicon once it has loaded, else its monogram. */
export function tabIconContent(tab: TabView): ReactNode {
	if (tab.icon) return <img className="tab-favicon" src={tab.icon} alt="" />
	return monogram(tab)
}

import { clipboard, shell, type MenuItemConstructorOptions } from 'electron'
import { tabOrganizationLabel, tabOrganizations } from '@shared/browser/tabOrganizer'
import type { TabId } from '@shared/model'
import type { TabOrdering } from './tab-ordering'
import type { TabService } from './tab-service'

export interface TabMenuContext {
	readonly tabs: TabService
	readonly ordering: TabOrdering
}

const WEB_URL = /^https?:/i

/** Close everything but this tab; pinned tabs are never closed this way. */
export function closeOtherTabs(tabs: TabService, keep: TabId): void {
	for (const record of [...tabs.records]) {
		if (record.id !== keep && !record.pinned) tabs.close(record.id)
	}
	tabs.activate(keep)
}

/** The right-click menu of a tab, shared by chips, stack chips and the rows of a stack's dropdown. */
export function tabMenuTemplate(ctx: TabMenuContext, id: TabId): MenuItemConstructorOptions[] {
	const { tabs } = ctx
	const record = tabs.get(id)
	const loose = tabs.records.some((t) => t.id !== id && !t.pinned)
	return [
		record.pinned
			? { label: 'Unpin Tab', click: () => tabs.unpin(id) }
			: { label: 'Pin Tab', click: () => tabs.pin(id) },
		{ type: 'separator' },
		{ label: 'Reload', click: () => tabs.reload(id) },
		{
			label: 'Open in Default Browser',
			enabled: WEB_URL.test(record.url),
			click: () => void shell.openExternal(record.url),
		},
		{ label: 'Copy Address', enabled: record.url !== 'about:blank', click: () => clipboard.writeText(record.url) },
		{ type: 'separator' },
		{ label: 'Close Tab', enabled: !record.pinned, click: () => tabs.close(id) },
		{ label: 'Close Other Tabs', enabled: loose, click: () => closeOtherTabs(tabs, id) },
	]
}

/** A stack chip offers its lead page's menu plus closing the whole stack. */
export function stackMenuTemplate(ctx: TabMenuContext, key: string): MenuItemConstructorOptions[] {
	const stack = ctx.ordering.stacks().find((s) => s.key === key && s.ids.length > 1)
	if (!stack) return []
	const lead = stack.ids.find((id) => id === ctx.tabs.activeId) ?? stack.ids[0]!
	return [
		...tabMenuTemplate(ctx, lead),
		{ type: 'separator' },
		{
			label: `Close All ${stack.ids.length} Tabs from ${stack.name}`,
			click: () => stack.ids.forEach((id) => ctx.tabs.close(id)),
		},
	]
}

/** Right-click on the empty strip: how tabs are arranged and drawn. */
export function stripMenuTemplate(ctx: TabMenuContext): MenuItemConstructorOptions[] {
	const { ordering } = ctx
	return [
		{
			label: 'Arrange Tabs',
			submenu: tabOrganizations.map(
				(organization): MenuItemConstructorOptions => ({
					label: tabOrganizationLabel[organization],
					type: 'radio',
					checked: ordering.organization === organization,
					click: () => ordering.setOrganization(organization),
				}),
			),
		},
		{
			label: 'Group Sites into Dropdowns',
			type: 'checkbox',
			checked: ordering.dropdowns,
			click: () => ordering.setDropdowns(!ordering.dropdowns),
		},
	]
}

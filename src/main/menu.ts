import { Menu, type BaseWindow, type MenuItemConstructorOptions } from 'electron'
import { MENU_LAYOUT, getCommand, toAccelerator, type CommandId, type MenuEntry } from '@shared/commands'

export type MenuCommandRunner = (id: CommandId, window: BaseWindow | undefined) => void

const EDIT_ROLES: MenuItemConstructorOptions[] = [
	{ role: 'undo' },
	{ role: 'redo' },
	{ type: 'separator' },
	{ role: 'cut' },
	{ role: 'copy' },
	{ role: 'paste' },
	{ role: 'selectAll' },
	{ type: 'separator' },
]

function menuItem(entry: MenuEntry, run: MenuCommandRunner): MenuItemConstructorOptions {
	if (entry === 'separator') return { type: 'separator' }
	const command = getCommand(entry)
	const [primary] = command.shortcuts
	return {
		id: command.id,
		label: command.label,
		...(primary && { accelerator: toAccelerator(primary) }),
		// Shown, not registered: keys are routed in keyboard.ts from every WebContents.
		registerAccelerator: false,
		click: (_item, window) => run(command.id, window),
	}
}

/** The application menu, built from the shared command registry. */
export function buildAppMenu(run: MenuCommandRunner): Menu {
	const template = MENU_LAYOUT.map(
		(section): MenuItemConstructorOptions => ({
			label: section.label,
			submenu: [
				...(section.label === 'Edit' ? EDIT_ROLES : []),
				...section.items.map((entry) => menuItem(entry, run)),
			],
		}),
	)
	return Menu.buildFromTemplate(template)
}

import { getCommand, shortcutLabel, type CommandId } from '@shared/commands'

/** "Back (Alt+Left)": the label and primary shortcut, straight from the registry. */
export function commandTooltip(id: CommandId): string {
	const command = getCommand(id)
	const [primary] = command.shortcuts
	return primary ? `${command.label} (${shortcutLabel(primary)})` : command.label
}

import type { WebContents } from 'electron'
import { commandForInput, type CommandId } from '@shared/commands'
import type { CommandOutcome } from '@shared/ipc'

/**
 * Routes registry shortcuts from any of the window's WebContents (chrome,
 * pages, overlay) before the page sees them. This is the one dispatch path for
 * keys; menu items show their accelerator without registering it, so nothing
 * fires twice. A command with nothing to act on lets the key through, so Esc
 * still reaches a page that isn't loading.
 */
export function routeShortcuts(contents: WebContents, run: (id: CommandId) => CommandOutcome): void {
	contents.on('before-input-event', (event, input) => {
		if (input.type !== 'keyDown') return
		const command = commandForInput(input)
		if (!command) return
		if (run(command.id).status !== 'not-applicable') event.preventDefault()
	})
}

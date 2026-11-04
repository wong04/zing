// Formats a document too large to parse on the main thread without freezing the tool.
import { formatJSON, type JSONMode, type JSONOutcome } from '@shared/devtools/jsonFormat'

export type JSONRequest = { readonly text: string; readonly mode: JSONMode }

self.onmessage = (event: MessageEvent<JSONRequest>) => {
	const reply: JSONOutcome = formatJSON(event.data.text, event.data.mode)
	self.postMessage(reply)
}

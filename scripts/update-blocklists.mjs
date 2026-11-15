// Refreshes the filter lists blockTrackers bundles (host/Zing.Core/Defaults, read by FilterRules.cs). They ship with each
// release rather than being fetched by Zing, so browsing never contacts the list's server.
import { writeFileSync } from 'node:fs'
import { join } from 'node:path'

const DEFAULTS = join(import.meta.dirname, '..', 'host', 'Zing.Core', 'Defaults')
const LISTS = ['easylist.txt', 'easyprivacy.txt']

for (const name of LISTS) {
	const response = await fetch(`https://easylist.to/easylist/${name}`)
	if (!response.ok) throw new Error(`${name}: HTTP ${response.status}`)
	const text = (await response.text()).replaceAll('\r\n', '\n')
	if (!text.startsWith('[Adblock Plus')) throw new Error(`${name} is not an Adblock Plus filter list`)
	writeFileSync(join(DEFAULTS, name), text)
	console.log(`${name}: ${text.match(/^! Version: (.*)$/m)?.[1] ?? 'no version line'}`)
}

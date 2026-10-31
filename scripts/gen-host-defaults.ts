import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { hostDefaults } from './host-defaults'

const dir = join(import.meta.dirname, '..', 'host', 'Zing.Core', 'Defaults')
for (const [name, value] of Object.entries(hostDefaults())) {
	writeFileSync(join(dir, name), `${JSON.stringify(value, null, '\t')}\n`)
	console.info(`wrote host/Zing.Core/Defaults/${name}`)
}

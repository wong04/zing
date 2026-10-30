import { safeFileBase, uniqueOutputName } from '../convert/commands'

/**
 * The file name a download is saved under: made safe for Windows, and numbered
 * ("report 2.pdf") rather than overwriting a file that is already there.
 */
export function downloadFileName(filename: string, exists: (name: string) => boolean): string {
	const dot = filename.lastIndexOf('.')
	const hasExt = dot > 0 && dot < filename.length - 1
	const base = safeFileBase(hasExt ? filename.slice(0, dot) : filename)
	if (hasExt) return uniqueOutputName(base, filename.slice(dot + 1), exists, true)
	// uniqueOutputName always joins with a dot; an extensionless file gets it trimmed back off.
	return uniqueOutputName(base, '', (candidate) => exists(candidate.slice(0, -1)), true).slice(0, -1)
}

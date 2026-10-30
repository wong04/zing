// Path pieces for the file components. Paths here are Windows paths as the
// main process hands them over, with either kind of separator.

function segments(path: string): string[] {
	return path.split(/[\\/]+/).filter((s) => s !== '')
}

export function fileName(path: string): string {
	return segments(path).at(-1) ?? path
}

/** The folder's own name, which is what a recents row shows under the file name. */
export function folderName(path: string): string {
	return segments(path).at(-2) ?? ''
}

/** Lowercase, without the dot; empty when there is none. A leading dot (".gitignore") is a name, not an extension. */
export function extension(path: string): string {
	const name = fileName(path)
	const dot = name.lastIndexOf('.')
	if (dot <= 0 || dot === name.length - 1) return ''
	return name.slice(dot + 1).toLowerCase()
}

// Reads the two things FFmpeg prints that say how far a conversion is: the length of its input (once, at the start) and
// the time of what it has made so far (on every progress line). The viewer shows a percentage from them.

const clock = (hours: string, minutes: string, seconds: string): number => Number(hours) * 3600 + Number(minutes) * 60 + Number(seconds)

/** The input's length in seconds, from a line such as `  Duration: 00:02:00.04, start: 0.000000`; none for N/A. */
export function durationOf(line: string): number | undefined {
	const match = /Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)/.exec(line)
	return match ? clock(match[1]!, match[2]!, match[3]!) : undefined
}

/** How much of the output exists, in seconds, from a progress line such as `frame= 240 ... time=00:00:10.00 ...`. */
export function elapsedOf(line: string): number | undefined {
	const match = /time=(-?)(\d+):(\d\d):(\d\d(?:\.\d+)?)/.exec(line)
	if (!match || match[1] === '-') return undefined
	return clock(match[2]!, match[3]!, match[4]!)
}

/** 0 to 1, or none while the input's length is unknown. */
export function conversionFraction(elapsed: number, total: number | undefined): number | undefined {
	if (total === undefined || total <= 0) return undefined
	return Math.min(1, elapsed / total)
}

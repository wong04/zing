// When a five-field cron expression fires next. Krackn's `describeCron` only reads the
// fields back; this answers the question people actually ask of one.
//
// Evaluated on the wall clock of the `from` date's own time zone, as cron does.

/** Carries a human-readable reason a cron expression can't be scheduled. */
export class CronProblem extends Error {
	override name = 'CronProblem'
}

type Spec = { readonly name: string; readonly min: number; readonly max: number; readonly names?: readonly string[] }

const specs: readonly Spec[] = [
	{ name: 'minute', min: 0, max: 59 },
	{ name: 'hour', min: 0, max: 23 },
	{ name: 'day of month', min: 1, max: 31 },
	{ name: 'month', min: 1, max: 12, names: ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'] },
	{ name: 'day of week', min: 0, max: 7, names: ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat'] },
]

// A February 29th is at most eight years away (a skipped century leap year); beyond that, nothing will ever match.
const searchDays = 366 * 8

type Schedule = {
	readonly minutes: ReadonlySet<number>
	readonly hours: ReadonlySet<number>
	readonly daysOfMonth: ReadonlySet<number>
	readonly months: ReadonlySet<number>
	readonly daysOfWeek: ReadonlySet<number>
	readonly anyDayOfMonth: boolean
	readonly anyDayOfWeek: boolean
}

/** The next `count` times after `from` the expression fires. Throws `CronProblem` if it is malformed or never fires. */
export function nextCronRuns(expression: string, from: Date, count: number): Date[] {
	const schedule = parse(expression)
	const runs: Date[] = []
	let after = from
	while (runs.length < count) {
		const next = nextRun(schedule, after)
		if (next === null) throw new CronProblem('This schedule never fires: no date matches its day and month.')
		runs.push(next)
		after = next
	}
	return runs
}

function parse(expression: string): Schedule {
	const values = expression.split(' ').filter((value) => value !== '')
	if (values.length !== specs.length) throw new CronProblem(`A cron expression has 5 fields, this has ${values.length}.`)
	const [minutes, hours, daysOfMonth, months, daysOfWeek] = values.map((value, index) => parseField(value, specs[index] as Spec))
	return {
		minutes: minutes as Set<number>,
		hours: hours as Set<number>,
		daysOfMonth: daysOfMonth as Set<number>,
		months: months as Set<number>,
		// 7 is Sunday too.
		daysOfWeek: new Set([...(daysOfWeek as Set<number>)].map((day) => day % 7)),
		anyDayOfMonth: values[2] === '*' || (values[2] as string).startsWith('*/'),
		anyDayOfWeek: values[4] === '*' || (values[4] as string).startsWith('*/'),
	}
}

function parseField(field: string, spec: Spec): Set<number> {
	const values = new Set<number>()
	for (const part of field.split(',')) {
		const [range = '', step, extra] = part.split('/')
		if (extra !== undefined) throw new CronProblem(`Invalid ${spec.name} "${field}".`)
		const size = step === undefined ? 1 : number(step, spec, field)
		if (size < 1) throw new CronProblem(`The step in ${spec.name} "${field}" must be at least 1.`)
		const [low, high] = bounds(range, spec, field, step !== undefined)
		for (let value = low; value <= high; value += size) values.add(value)
	}
	return values
}

function bounds(range: string, spec: Spec, field: string, stepped: boolean): [number, number] {
	if (range === '*') return [spec.min, spec.max === 7 ? 6 : spec.max]
	const [first = '', last, extra] = range.split('-')
	if (extra !== undefined) throw new CronProblem(`Invalid ${spec.name} "${field}".`)
	const low = number(first, spec, field)
	if (last !== undefined) {
		const high = number(last, spec, field)
		if (high < low) throw new CronProblem(`The ${spec.name} range "${range}" runs backwards.`)
		return [low, high]
	}
	// "5/10" runs from 5 to the end of the field.
	return [low, stepped ? spec.max : low]
}

function number(text: string, spec: Spec, field: string): number {
	const named = spec.names?.indexOf(text.toLowerCase())
	const value = named !== undefined && named >= 0 ? named + (spec.name === 'month' ? 1 : 0) : /^\d+$/.test(text) ? Number(text) : Number.NaN
	if (Number.isNaN(value)) throw new CronProblem(`"${text}" in ${spec.name} "${field}" is not a number.`)
	if (value < spec.min || value > spec.max) throw new CronProblem(`${spec.name} ${value} is outside ${spec.min}-${spec.max}.`)
	return value
}

function nextRun(schedule: Schedule, from: Date): Date | null {
	// Whole minutes, strictly after `from`.
	const start = new Date(from.getFullYear(), from.getMonth(), from.getDate(), from.getHours(), from.getMinutes() + 1)
	for (let offset = 0; offset < searchDays; offset++) {
		const day = new Date(start.getFullYear(), start.getMonth(), start.getDate() + offset)
		if (!runsOn(schedule, day)) continue
		const found = firstTimeOn(schedule, day, offset === 0 ? start : null)
		if (found !== null) return found
	}
	return null
}

function runsOn(schedule: Schedule, day: Date): boolean {
	if (!schedule.months.has(day.getMonth() + 1)) return false
	const ofMonth = schedule.daysOfMonth.has(day.getDate())
	const ofWeek = schedule.daysOfWeek.has(day.getDay())
	// Cron's rule: with both day fields restricted, either one matching is enough.
	if (!schedule.anyDayOfMonth && !schedule.anyDayOfWeek) return ofMonth || ofWeek
	return ofMonth && ofWeek
}

function firstTimeOn(schedule: Schedule, day: Date, notBefore: Date | null): Date | null {
	for (const hour of [...schedule.hours].sort((a, b) => a - b)) {
		for (const minute of [...schedule.minutes].sort((a, b) => a - b)) {
			const candidate = new Date(day.getFullYear(), day.getMonth(), day.getDate(), hour, minute)
			if (notBefore === null || candidate >= notBefore) return candidate
		}
	}
	return null
}

// Port of `describeCron` from Krackn Features/Utilities/DevTools.swift.
//
// Explains a cron expression in plain English — these are famously easy to
// get wrong and hard to read back. Like Krackn, it reads the five fields back
// as written and does not validate their values.

type Field = { readonly name: string; readonly plural: string }

const fields: readonly Field[] = [
	{ name: 'minute', plural: 'minutes' },
	{ name: 'hour', plural: 'hours' },
	{ name: 'day of month', plural: 'days of the month' },
	{ name: 'month', plural: 'months' },
	{ name: 'day of week', plural: 'days of the week' },
]

/** Null unless the expression has exactly five space-separated fields. */
export function describeCron(expression: string): string | null {
	const values = expression.split(' ').filter((value) => value !== '')
	if (values.length !== fields.length) return null
	const parts = values.map((value, index) => describeField(value, fields[index] as Field))
	return `Runs at ${parts.join(', ')}.`
}

function describeField(value: string, field: Field): string {
	if (value === '*') return `every ${field.name}`
	if (value.startsWith('*/')) return `every ${value.slice(2)} ${field.plural}`
	return `${field.name} ${value}`
}

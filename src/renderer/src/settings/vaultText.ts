/** The Saved passwords row's detail: how many logins, and when Windows Hello is asked (spec decision 3, Ruling 44). */
export function loginsDetail(count: number): string {
	return `${count === 1 ? '1 login' : `${count} logins`}. Showing a password asks for Windows Hello, at most once every 5 minutes.`
}

/** A login row's line that opens its password history. */
export function earlierPasswords(count: number): string {
	return count === 1 ? '1 earlier password' : `${count} earlier passwords`
}

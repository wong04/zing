import { RelayError } from '../../shared/unblock/relayEngine'

/** DoH answered but had no A record to connect to. */
export class NoAddressError extends RelayError {
	override name = 'NoAddressError'
	constructor(readonly host: string) {
		super(`${host} has no address records.`)
	}
}

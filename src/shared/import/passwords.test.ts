import { describe, expect, it } from 'vitest'
import { CsvSyntaxError } from './csv'
import { parsePasswordCsv, UnknownPasswordCsvError } from './passwords'
import vectors from './passwords.vectors.json'

// The C# twin, Zing.Core.Vault.VaultCsv, reads the same cases (VaultCsvTests): the host does the importing.
describe('password CSV vectors', () => {
	it.each(vectors.parse)('reads $name', ({ csv, format, logins, skipped }) => {
		expect(parsePasswordCsv(csv)).toEqual({ format, logins, skipped })
	})

	it.each(vectors.refused)('refuses $name', ({ csv, error }) => {
		expect(() => parsePasswordCsv(csv)).toThrow(error === 'unknown' ? UnknownPasswordCsvError : CsvSyntaxError)
	})
})

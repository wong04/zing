import { resolve } from 'node:path'
import { defineConfig } from 'vitest/config'

export default defineConfig({
	resolve: { alias: { '@shared': resolve('src/shared') } },
	test: {
		include: ['src/**/*.test.ts', 'scripts/**/*.test.ts'],
		// These call outside services; npm test must not reach the internet.
		exclude: ['src/**/*.network.test.ts', 'node_modules/**'],
		passWithNoTests: false,
	},
})

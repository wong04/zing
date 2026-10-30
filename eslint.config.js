import js from '@eslint/js'
import tseslint from 'typescript-eslint'
import reactHooks from 'eslint-plugin-react-hooks'

export default tseslint.config(
	{ ignores: ['out/**', 'dist/**', 'release/**', 'reference/**', 'node_modules/**'] },
	js.configs.recommended,
	...tseslint.configs.recommended,
	{
		// Plain Node scripts run outside the TypeScript build.
		files: ['scripts/**/*.mjs'],
		languageOptions: { globals: { Buffer: 'readonly', process: 'readonly', console: 'readonly', setTimeout: 'readonly' } },
	},
	{
		files: ['src/renderer/**/*.{ts,tsx}'],
		plugins: { 'react-hooks': reactHooks },
		rules: reactHooks.configs.recommended.rules,
	},
)

import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { DELETE_SYNC_CONFIRM, ForgotSyncPassword } from './SyncSetup'

vi.mock('../api', () => ({ zing: {} }))

const render = (typed: string): string =>
	renderToStaticMarkup(
		<ForgotSyncPassword typed={typed} onTyped={() => {}} busy={false} error={undefined} onBack={() => {}} onStartOver={() => {}} onDelete={() => {}} />,
	)

const deleteButton = (html: string): string => {
	const match = html.match(/<button[^>]*>(?:(?!<\/button>).)*Sign in with Google and delete the sync account/s)
	if (!match) throw new Error('no delete button')
	return match[0]
}

describe('deleting only the sync account from Join sync', () => {
	it('says the server copy goes for good and this PC keeps its account and passwords', () => {
		const html = render('')
		expect(html).toContain('deletes it and its encrypted passwords on the sync server')
		expect(html).toContain('can’t be brought back')
		expect(html).toContain('This PC keeps its Zing account and passwords')
		expect(html).toContain(`Type ${DELETE_SYNC_CONFIRM} to confirm`)
	})

	it('deletes nothing until the confirmation is typed exactly', () => {
		expect(deleteButton(render(''))).toContain('disabled')
		expect(deleteButton(render('delete sync account'))).toContain('disabled')
		expect(deleteButton(render(DELETE_SYNC_CONFIRM))).not.toContain('disabled')
	})
})

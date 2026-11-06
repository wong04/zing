import { launchZing, test } from './zing'
test('debug', async () => {
	const zing = await launchZing()
	for (const path of ['/zing-local/model/ffmpeg', '/zing-local/', '/zing-local/file/' + '0'.repeat(32), '/zing-local/model/bgmodel', '/nothing-here']) {
		const r = await zing.chrome.evaluate((p) => fetch(p).then((x) => 'status ' + x.status, (e: Error) => e.name + ' ' + e.message), path)
		process.stdout.write(`${path} => ${r}\n`)
	}
	await zing.close()
})

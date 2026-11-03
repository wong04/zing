import type { ToolLoaders } from '../registry'

// One entry per tool, keyed by `panelId` ("tool:Hashes"). Each loads on first open.
export const devTools: ToolLoaders = {
	'tool:JSON': () => import('./JsonTool').then((m) => ({ default: m.JsonTool })),
	'tool:Base64': () => import('./Base64Tool').then((m) => ({ default: m.Base64Tool })),
	'tool:URL Encoding': () => import('./UrlTool').then((m) => ({ default: m.UrlTool })),
	'tool:Regex Tester': () => import('./RegexTool').then((m) => ({ default: m.RegexTool })),
	'tool:UUID': () => import('./UuidTool').then((m) => ({ default: m.UuidTool })),
	'tool:Timestamps': () => import('./TimestampTool').then((m) => ({ default: m.TimestampTool })),
	'tool:Cron Parser': () => import('./CronTool').then((m) => ({ default: m.CronTool })),
	'tool:Hashes': () => import('./HashTool').then((m) => ({ default: m.HashTool })),
	'tool:HMAC': () => import('./HmacTool').then((m) => ({ default: m.HmacTool })),
	'tool:JWT Decoder': () => import('./JwtTool').then((m) => ({ default: m.JwtTool })),
}

// The bridge contract's channel list, read from the two interfaces in
// src/shared/ipc.ts with the TypeScript parser (types are gone at runtime).
// The C# host checks its handlers against it (ChannelContractTests).
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { join } from 'node:path'
import type * as TS from 'typescript'

// Loaded at run time, not bundled: the generator is a rolldown bundle, and the
// compiler doesn't survive being inlined into an ES module.
const ts = createRequire(import.meta.url)('typescript') as typeof TS

export const IPC_SOURCE = join(import.meta.dirname, '..', 'src', 'shared', 'ipc.ts')

/** An invoke channel and how many arguments it takes; `minArgs`/`maxArgs` replace `args` when some are optional. */
export type InvokeChannelEntry =
	| { readonly channel: string; readonly args: number }
	| { readonly channel: string; readonly minArgs: number; readonly maxArgs: number }

export interface HostChannels {
	readonly invoke: readonly InvokeChannelEntry[]
	readonly events: readonly string[]
}

/** Ordinal, like C#'s StringComparer.Ordinal, so neither side depends on a locale. */
const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0)

function contractInterface(source: TS.SourceFile, name: string): TS.InterfaceDeclaration {
	const found = source.statements.find((s): s is TS.InterfaceDeclaration => ts.isInterfaceDeclaration(s) && s.name.text === name)
	if (!found) throw new Error(`${name} not found in ipc.ts`)
	return found
}

function channelName(member: TS.TypeElement, contract: string): string {
	if (!ts.isPropertySignature(member) || !member.name || !(ts.isStringLiteral(member.name) || ts.isIdentifier(member.name))) {
		throw new Error(`${contract} has a member that isn't a named channel`)
	}
	return member.name.text
}

function arity(member: TS.PropertySignature, channel: string): InvokeChannelEntry {
	const type = member.type
	const args = type && ts.isTypeLiteralNode(type) ? type.members.find((m) => m.name && ts.isIdentifier(m.name) && m.name.text === 'args') : undefined
	const tuple = args && ts.isPropertySignature(args) ? args.type : undefined
	if (!tuple || !ts.isTupleTypeNode(tuple)) throw new Error(`${channel}: args must be a tuple`)
	let min = 0
	for (const element of tuple.elements) {
		const rest = ts.isRestTypeNode(element) || (ts.isNamedTupleMember(element) && element.dotDotDotToken)
		if (rest) throw new Error(`${channel}: rest arguments have no fixed arity`)
		const optional = ts.isOptionalTypeNode(element) || (ts.isNamedTupleMember(element) && element.questionToken)
		if (!optional) min++
	}
	const max = tuple.elements.length
	return min === max ? { channel, args: max } : { channel, minArgs: min, maxArgs: max }
}

export function hostChannels(path = IPC_SOURCE): HostChannels {
	const source = ts.createSourceFile(path, readFileSync(path, 'utf8'), ts.ScriptTarget.Latest, true)
	const invoke = contractInterface(source, 'InvokeContract').members.map((member) => arity(member as TS.PropertySignature, channelName(member, 'InvokeContract')))
	const events = contractInterface(source, 'EventContract').members.map((member) => channelName(member, 'EventContract'))
	return {
		invoke: invoke.sort((a, b) => ordinal(a.channel, b.channel)),
		events: events.sort(ordinal),
	}
}

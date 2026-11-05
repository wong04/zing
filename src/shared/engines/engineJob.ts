// Running an engine and picking files (host/Zing.Core/Engines/EngineRunner.cs, FileGrants.cs).

/** What the picker is asked for; the host turns each into a list of extensions. */
export type FileKind = 'image' | 'video' | 'audio' | 'document' | 'any'

/**
 * A file the person picked or dropped. The renderer never sees its path: the token is what an engine job takes.
 * Only a token the host issued to this window works, so a path made up here reaches no file.
 */
export interface PickedFile {
	readonly token: string
	/** The file's name, for showing. */
	readonly name: string
}

/**
 * One argument of an engine job. A string is option text ("-c:v", "scale=iw/2:ih/2"): it can't name a file, so a rooted
 * path, a backslash, ".." or a file: address is rejected. `input` is a picked or dropped file, passed as an absolute path
 * (so a file named "-rf.mov" can't be read as an option). `output` is the temporary path of the job's n-th output.
 */
export type JobArg = string | { readonly input: string } | { readonly output: number }

export interface EngineJobSpec {
	readonly args: readonly JobArg[]
	/**
	 * The files the job makes, by name. They are made in a private folder and moved into the Downloads folder only if the
	 * engine exits 0 and nothing cancelled it (all of them or none), numbered ("clip 2.mp4") rather than overwriting.
	 */
	readonly outputs?: readonly string[]
	/** For yt-dlp and the like: http or https addresses, passed after a "--" so none can be an option. */
	readonly urls?: readonly string[]
}

export interface EngineOutputLine {
	readonly stream: 'out' | 'err'
	/** At most 2000 characters; a longer line ends with an ellipsis. */
	readonly text: string
}

/** Batched, at most 200 lines. When a flood outruns the bridge, the oldest lines are dropped and counted in `skipped`. */
export interface EngineOutput {
	readonly jobId: string
	readonly lines: readonly EngineOutputLine[]
	readonly skipped: number
}

/** Pushed exactly once per job, after every line of output and after the outputs are in place. */
export interface EngineExit {
	readonly jobId: string
	readonly code: number
	/** The job was stopped (Cancel, its panel closed) before it finished. */
	readonly cancelled: boolean
	/** The final paths of the outputs, in the order named in the spec. Empty unless the engine exited 0 and nothing cancelled it. */
	readonly outputs: readonly string[]
	/** The last lines the engine wrote to stderr, for an error message. */
	readonly stderrTail: readonly string[]
	/** Set when the outputs couldn't be put in place; the engine's own failure is `code` and `stderrTail`. */
	readonly error?: string
}

// Running an engine and picking files (host/Zing.Core/Engines/EngineRunner.cs, FileGrants.cs).

/** What the picker is asked for; the host turns each into a list of extensions. */
export type FileKind = 'image' | 'video' | 'audio' | 'document' | 'any'

/**
 * A file the person picked or dropped, or an output a job made. The host does not hand the renderer its path: the token
 * is what an engine job takes. Only a token the host issued to this window works, so a path made up here reaches no file.
 * (An engine's own output lines can still mention paths.)
 */
export interface PickedFile {
	readonly token: string
	/** The file's name, for showing. */
	readonly name: string
}

/**
 * One argument of an engine job. A string must be a flag on the host's allow-list for that engine (host/Zing.Core/Engines/
 * EngineFlagPolicy.cs) or the value of one, in the shape the flag declares (a number, a short word, or one of a fixed set).
 * It is never free text, so it can't name a file, an address or a program; anything else is refused by name. Tools build
 * option text from fixed constants and add a flag to the list, reviewed, when they need a new one. `input` is a picked or
 * dropped file, passed as an absolute path (so a file named "-rf.mov" can't be read as an option). `output` is the
 * temporary path of the job's n-th output. The runner adds each engine's own safety flags; they can't be left out.
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

/** What the engine printed; may mention file paths. Batched, at most 200 lines. When a flood outruns the bridge, the oldest lines are dropped and counted in `skipped`. */
export interface EngineOutput {
	readonly jobId: string
	readonly lines: readonly EngineOutputLine[]
	readonly skipped: number
}

/**
 * Pushed exactly once per job, after every line of output and after the outputs are in place.
 *
 * A job that was cancelled while its file was being placed keeps the file: `cancelled` is false and `outputs` has it. Word
 * the UI to match ("finished" rather than "cancelled").
 */
export interface EngineExit {
	readonly jobId: string
	readonly code: number
	/** The job was stopped (Cancel, its panel closed) before its file was placed. */
	readonly cancelled: boolean
	/**
	 * The outputs, in the order named in the spec: a name to show and a token that stands for the file (usable as an `input`
	 * of a later job), not a path. Empty unless the engine exited 0 and nothing cancelled it.
	 */
	readonly outputs: readonly PickedFile[]
	/**
	 * The last lines the engine wrote to stderr, for an error message. Like the output lines, they can contain file paths
	 * (FFmpeg prints the files it reads); show them, don't parse or store them.
	 */
	readonly stderrTail: readonly string[]
	/** Set when the outputs couldn't be put in place, or the runner failed; the engine's own failure is `code` and `stderrTail`. */
	readonly error?: string
}

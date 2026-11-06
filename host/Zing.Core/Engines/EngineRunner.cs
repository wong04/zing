using System.Diagnostics;
using System.Text;

namespace Zing.Core.Engines;

/// <summary>One argument of an engine job. The renderer can only send these three shapes.</summary>
public abstract record JobArg
{
	/// <summary>Option text such as "-c:v" or "scale=iw/2:ih/2". It can't name a file: see <see cref="EngineRunner.Start"/>.</summary>
	public sealed record Literal(string Text) : JobArg;

	/// <summary>A file the host already vetted (a picked or dropped one); passed as an absolute path.</summary>
	public sealed record Input(string Path) : JobArg;

	/// <summary>The temporary path of the job's n-th output, by position in <see cref="EngineJobRequest.OutputNames"/>.</summary>
	public sealed record Output(int Index) : JobArg;
}

/// <param name="OutputDirectory">Where finished outputs are placed.</param>
/// <param name="OutputNames">The file names the job will make, in order. Each is placed under this name, numbered ("clip 2.mp4") if it is taken, unless "Never overwrite files" is off.</param>
/// <param name="Urls">Addresses for tools like yt-dlp; appended after a "--".</param>
/// <param name="OutputsStaged">Called before the engine starts with the temporary path of each output, in order. A tool that shows the
/// file while it grows (the viewer) reads it from there; the path stops existing when the file is placed or the job ends.</param>
public sealed record EngineJobRequest(string EngineId, IReadOnlyList<JobArg> Args, string OutputDirectory, IReadOnlyList<string> OutputNames, IReadOnlyList<string> Urls, Action<IReadOnlyList<string>>? OutputsStaged = null);

/// <summary>A job belongs to the panel that started it, in the window that holds the panel.</summary>
public sealed record JobOwner(string Window, string Panel);

public sealed record JobOutput(string Stream, string Text);

/// <param name="Skipped">Older lines dropped to keep the batch to <see cref="EngineRunner.MaxBatchLines"/>; the newest are kept.</param>
public sealed record JobOutputBatch(string JobId, IReadOnlyList<JobOutput> Lines, int Skipped);

/// <param name="Outputs">The final paths, only when the engine exited 0 and nothing cancelled it; all of them or none.</param>
/// <param name="StderrTail">The last lines the engine wrote to stderr, for an error message.</param>
/// <param name="Error">Set when the engine's output couldn't be put in place, or the runner failed; the engine's own failure is in Code and StderrTail.</param>
public sealed record JobExit(string JobId, int Code, bool Cancelled, IReadOnlyList<string> Outputs, IReadOnlyList<string> StderrTail, string? Error);

/// <summary>
/// Runs a downloaded engine as a child process for one job. The executable comes only from <see cref="EngineStore"/>;
/// arguments go through ArgumentList, never a shell. Every literal must be a flag on that engine's allow-list
/// (<see cref="EngineFlagPolicy"/>) with a value of a strict shape, so option text can't name a file, an address or a
/// program; files are only tokens the person was given and outputs only the job's own. Safety flags per engine are
/// always added. The process starts with no console window, a scrubbed environment, an empty private folder as its
/// working directory (so a program or DLL planted beside someone's input isn't picked up from it) and no stdin. Each job
/// has a Win32 job object, so it ends with Zing, and cancelling ends the whole process tree. An engine writes to a temp
/// file in the job's folder and the file is placed only on exit code 0 and no cancel, numbered or replacing as "Never overwrite files" says
/// (staged beside its final name when the folders are on different volumes); the job's folder is then deleted. Nothing
/// is logged or kept on disk.
/// </summary>
public sealed class EngineRunner
{
	public const int MaxRunning = 3;
	public const int MaxBatchLines = 200;
	public const int MaxLineChars = 2000;
	public const int StderrTailLines = 20;
	private const int RememberedEnded = 64;

	private readonly EngineStore _store;
	private readonly string _root;
	private readonly OutputPlacement _placement;
	private readonly Action<string, Exception> _onError;
	private readonly TimeSpan _flushEvery;
	private readonly EngineFlagPolicy _policy;
	private readonly object _gate = new();
	private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _ended = new(StringComparer.Ordinal);
	private readonly Queue<string> _endedOrder = new();

	/// <param name="root">Where jobs' private folders go, under Zing's data. Anything left in it from a Zing that was killed is deleted now.</param>
	/// <param name="onError">Called for a failure nobody awaits: a folder that couldn't be deleted (tried again at the next start) or a callback that threw.</param>
	/// <param name="placement">Puts each output in its folder, numbered or replacing, as the person's setting says.</param>
	public EngineRunner(EngineStore store, string root, OutputPlacement placement, Action<string, Exception> onError, TimeSpan? flushEvery = null)
		: this(store, root, placement, onError, EngineFlagPolicy.Pinned, flushEvery)
	{
	}

	internal EngineRunner(EngineStore store, string root, OutputPlacement placement, Action<string, Exception> onError, EngineFlagPolicy policy, TimeSpan? flushEvery = null)
	{
		_store = store;
		_root = root;
		_placement = placement;
		_onError = onError;
		_policy = policy;
		_flushEvery = flushEvery ?? TimeSpan.FromMilliseconds(100);
		RemoveLeftovers();
	}

	/// <summary>
	/// A runner that also accepts the flags of Zing.FakeEngine, for e2e runs that serve it as every engine. Zing.Host calls it
	/// only in an e2e run with a fixture catalog (ZING_ENGINE_BASE_URL); a real run uses the public constructor.
	/// </summary>
	public static EngineRunner ForFakeEngine(EngineStore store, string root, OutputPlacement placement, Action<string, Exception> onError) =>
		new(store, root, placement, onError, EngineFlagPolicy.WithFakeEngine());

	public int Running
	{
		get
		{
			lock (_gate) return _jobs.Count;
		}
	}

	/// <summary>Test seam: runs before each attempt to move an output into place, with the job's id.</summary>
	internal Action<string>? BeforeMove { get; set; }

	/// <summary>Test seam: runs between the process starting and joining its job object, with the job's id.</summary>
	internal Action<string>? AfterStart { get; set; }

	/// <summary>Test seam: whether two paths are on one volume (a rename); false stages a copy beside the final name.</summary>
	internal Func<string, string, bool> SameVolume { get; set; } = (a, b) =>
		string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

	/// <summary>How long the output pipes may stay open after the engine and its job have ended (something outside the job can hold them).</summary>
	internal TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>Test seam: awaited with the output pipes, so a test can hold the drain open or make it fail.</summary>
	internal Task? DrainBlocker { get; set; }

	/// <summary>
	/// Starts a job and returns its id at once; <paramref name="onOutput"/> and then, exactly once, <paramref name="onExit"/>
	/// follow on another thread. The exit comes after every line of output and after the outputs are in place.
	/// </summary>
	/// <exception cref="UnknownEngineException">Not an id in the catalog.</exception>
	/// <exception cref="EngineNotInstalledException">Not downloaded; running never downloads.</exception>
	/// <exception cref="UnsafeEngineArgumentException">A literal that isn't an allowed flag with a value of its shape (<see cref="DisallowedEngineFlagException"/> names the flag), a bad output name or extension, or a bad URL.</exception>
	/// <exception cref="EngineBusyException">The engine is being downloaded or updated.</exception>
	/// <exception cref="TooManyJobsException"><see cref="MaxRunning"/> jobs are already running.</exception>
	public string Start(JobOwner owner, EngineJobRequest request, Action<JobOutputBatch> onOutput, Action<JobExit> onExit)
	{
		var engine = _store.Catalog.Get(request.EngineId);
		var policy = _policy.For(engine.Id);
		policy.Check(request.Args);
		var names = request.OutputNames.Select(name => SafeOutputName(name, policy)).ToList();
		foreach (var arg in request.Args) Validate(arg, names.Count);
		if (request.Urls.Count > 0 && !policy.Network) throw new UnsafeEngineArgumentException($"{engine.Name} doesn't take addresses");
		var urls = request.Urls.Count == 0 ? [] : EngineArguments.Urls(request.Urls);
		// Looked up before any lease is taken, so a missing FFmpeg leaves nothing to undo.
		var ffmpeg = policy.UsesFfmpeg ? _store.InstalledPath(EngineIds.Ffmpeg) : null;
		var lease = Lease(engine.Id, ffmpeg is not null);
		var id = Guid.NewGuid().ToString("N");
		var folder = Path.Combine(_root, id);
		JobObject? tree = null;
		Job? job = null;
		try
		{
			var executable = _store.InstalledPath(request.EngineId);
			// The job object exists before the job is findable, so a close that arrives at any moment has something to end.
			tree = JobObject.Create();
			job = new Job(id, owner, folder, request.OutputDirectory, names, lease, tree, onOutput, onExit);
			lock (_gate)
			{
				if (_jobs.Count >= MaxRunning) throw new TooManyJobsException(MaxRunning);
				_jobs[id] = job;
			}
			Directory.CreateDirectory(job.WorkFolder);
			Directory.CreateDirectory(job.TempFolder);
			for (var i = 0; i < names.Count; i++) Directory.CreateDirectory(Path.GetDirectoryName(job.TempOutputs[i])!);
			if (engine.Id == EngineIds.Magick) WriteMagickPolicy(job);
			request.OutputsStaged?.Invoke(job.TempOutputs);
			var process = StartProcess(job, executable, ffmpeg, request, policy, urls);
			job.Process = process;
			AfterStart?.Invoke(id);
			JoinJob(job, process);
		}
		catch
		{
			Abandon(job, tree, lease, folder, id);
			throw;
		}
		_ = Task.Run(() => RunAsync(job));
		return id;
	}

	/// <summary>Stops one of this window's jobs. A job that has already ended is not an error.</summary>
	/// <exception cref="UnknownJobException">No such job in this window.</exception>
	public void Cancel(string jobId, string window)
	{
		lock (_gate)
		{
			if (_jobs.TryGetValue(jobId, out var job) && job.Owner.Window == window)
			{
				TryCancel(job);
				return;
			}
			if (_ended.TryGetValue(jobId, out var endedIn) && endedIn == window) return;
		}
		throw new UnknownJobException(jobId);
	}

	/// <summary>The window's panels now open (minimized ones included) are <paramref name="openPanels"/>; the jobs of any other panel stop.</summary>
	public void KillPanelsExcept(string window, IReadOnlySet<string> openPanels) =>
		CancelWhere(job => job.Owner.Window == window && !openPanels.Contains(job.Owner.Panel));

	public void KillWindow(string window) => CancelWhere(job => job.Owner.Window == window);

	private void CancelWhere(Func<Job, bool> matches)
	{
		lock (_gate)
		{
			foreach (var job in _jobs.Values.Where(matches)) TryCancel(job);
		}
	}

	/// <summary>
	/// Stops the job unless it has already begun placing its output: then the engine has finished and the file is
	/// made, and a close arriving in that instant doesn't undo it. Called with the table locked, so the job's
	/// handle is still open.
	/// </summary>
	private static void TryCancel(Job job)
	{
		lock (job.Sync)
		{
			if (job.Committing) return;
			job.Cancelled = true;
		}
		job.Tree.Terminate();
	}

	private static void Validate(JobArg arg, int outputs)
	{
		switch (arg)
		{
			case JobArg.Input input:
				EngineArguments.FilePath(input.Path);
				break;
			case JobArg.Output output when output.Index < 0 || output.Index >= outputs:
				throw new UnsafeEngineArgumentException($"Output {output.Index} isn't one the job named ({outputs} named)");
		}
	}

	private static readonly HashSet<string> NeverAnOutput = new(StringComparer.Ordinal)
	{
		"exe", "bat", "cmd", "ps1", "lnk", "scr", "msi", "com", "vbs", "vbe", "js", "jse", "wsf", "hta", "dll", "cpl", "reg", "pif", "url", "scf", "msc", "jar",
	};

	/// <summary>A plain file name whose extension the engine's list allows. Reserved device names (CON, NUL, COM1...) are renamed by <see cref="DownloadNames"/> rather than refused.</summary>
	private static string SafeOutputName(string name, EnginePolicy policy)
	{
		var plain = name.Length > 0 && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.TrimEnd('.', ' ').Length > 0;
		if (!plain) throw new UnsafeEngineArgumentException($"\"{name}\" isn't a plain file name");
		// ImageMagick expands "%d" and reads "[0]" in the name it writes; yt-dlp's -o is a template where "%" means something.
		if (name.IndexOfAny(['%', '[', ']']) >= 0) throw new UnsafeEngineArgumentException($"\"{name}\" has a \"%\", \"[\" or \"]\" in it, which an engine reads as a pattern");
		var cleaned = DownloadNames.FileName(name, _ => false);
		var extension = Path.GetExtension(cleaned).TrimStart('.').ToLowerInvariant();
		var allowed = extension.Length > 0 && !NeverAnOutput.Contains(extension) && policy.OutputExtensions.Contains(extension, StringComparer.Ordinal);
		if (!allowed) throw new UnsafeEngineArgumentException($"\"{name}\" isn't a kind of file {EngineCatalog.NameOf(policy.EngineId)} may make here");
		return cleaned;
	}

	private static void WriteMagickPolicy(Job job)
	{
		Directory.CreateDirectory(job.MagickFolder);
		File.WriteAllText(Path.Combine(job.MagickFolder, MagickPolicy.FileName), MagickPolicy.Xml);
	}

	private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy"];

	/// <summary>
	/// Nothing of Zing's or the person's environment reaches an engine: only the Windows folder (Python and winsock fail
	/// without it, and it holds no secret), the job's own TEMP, a PATH of the engine's folder alone (System32 is searched for
	/// DLLs whatever PATH says, so none of these needs it), the proxy variables for the one engine with the network, and
	/// ImageMagick's policy folder.
	/// </summary>
	private static void ScrubEnvironment(ProcessStartInfo start, Job job, string executable, EnginePolicy policy)
	{
		var env = start.Environment;
		env.Clear();
		var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		env["SystemRoot"] = windows;
		env["windir"] = windows;
		env["TEMP"] = job.TempFolder;
		env["TMP"] = job.TempFolder;
		env["PATH"] = Path.GetDirectoryName(executable)!;
		if (policy.EngineId == EngineIds.Magick) env["MAGICK_CONFIGURE_PATH"] = job.MagickFolder;
		if (!policy.Network) return;
		foreach (var name in ProxyVariables)
		{
			if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) env[name] = value;
		}
	}

	/// <summary>The engine's lease, and FFmpeg's too when the engine calls it, so neither can be removed under a running job.</summary>
	private IDisposable Lease(string engineId, bool withFfmpeg)
	{
		var lease = _store.Use(engineId);
		if (!withFfmpeg) return lease;
		try
		{
			return new Leases(lease, _store.Use(EngineIds.Ffmpeg));
		}
		catch
		{
			lease.Dispose();
			throw;
		}
	}

	private sealed class Leases(params IDisposable[] parts) : IDisposable
	{
		public void Dispose()
		{
			foreach (var part in parts) part.Dispose();
		}
	}

	private Process StartProcess(Job job, string executable, string? ffmpeg, EngineJobRequest request, EnginePolicy policy, IReadOnlyList<string> urls)
	{
		var start = new ProcessStartInfo(executable)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = job.WorkFolder,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		ScrubEnvironment(start, job, executable, policy);
		foreach (var flag in policy.SafetyFirst) start.ArgumentList.Add(flag);
		// Where FFmpeg is stays the host's to say: the policy has no flag a job could use to name a program.
		if (ffmpeg is not null)
		{
			start.ArgumentList.Add("--ffmpeg-location");
			start.ArgumentList.Add(ffmpeg);
		}
		foreach (var arg in request.Args)
		{
			if (arg is JobArg.Literal flag && policy.SafetyBefore.TryGetValue(flag.Text, out var before))
			{
				foreach (var safety in before) start.ArgumentList.Add(safety);
			}
			start.ArgumentList.Add(arg switch
			{
				JobArg.Literal literal => literal.Text,
				JobArg.Input input => EngineArguments.FilePath(input.Path),
				JobArg.Output output => OutputArgument(request.EngineId, job, output.Index),
				_ => throw new InvalidOperationException($"Unknown job argument {arg}"),
			});
		}
		foreach (var url in urls) start.ArgumentList.Add(url);
		return Process.Start(start) ?? throw new InvalidOperationException($"{executable} didn't start");
	}

	/// <summary>
	/// ImageMagick expands "%d" and reads "[0]" in the name it writes, so an output under a folder with either in its path
	/// (a user-data folder can have a "%") would fail. It is given the path relative to its working folder, which has none.
	/// </summary>
	private static string OutputArgument(string engineId, Job job, int index) =>
		engineId == EngineIds.Magick ? Path.GetRelativePath(job.WorkFolder, job.TempOutputs[index]) : job.TempOutputs[index];

	/// <summary>
	/// .NET can't start a process suspended, so it runs for the instant before it joins its job. A child it started in that
	/// instant would not be in the job: an engine takes far longer than that to start one (its runtime loads first). A close
	/// that arrived meanwhile ended an empty job, so it is applied again now.
	/// </summary>
	private static void JoinJob(Job job, Process process)
	{
		try
		{
			job.Tree.Assign(process);
		}
		catch
		{
			process.Kill(entireProcessTree: true);
			throw;
		}
		bool cancelled;
		lock (job.Sync) cancelled = job.Cancelled;
		if (cancelled) job.Tree.Terminate();
		process.StandardInput.Close();
	}

	private async Task RunAsync(Job job)
	{
		var outputs = new List<string>();
		string? error = null;
		var code = -1;
		try
		{
			var process = job.Process!;
			using var stopFlushing = new CancellationTokenSource();
			var flushing = FlushLoopAsync(job, stopFlushing.Token);
			var pumps = Task.WhenAll(PumpAsync(job, process.StandardOutput, "out"), PumpAsync(job, process.StandardError, "err"), DrainBlocker ?? Task.CompletedTask);
			await process.WaitForExitAsync();
			// Anything the engine left running holds the pipes open and would keep the output from draining; none should outlive it.
			job.Tree.Terminate();
			if (await Task.WhenAny(pumps, Task.Delay(DrainTimeout)) == pumps)
			{
				await pumps;
			}
			else
			{
				// Something outside the job holds the pipes; the lines it would still send are lost.
				error = "The engine's output didn't finish (something kept its pipes open), so the last lines may be missing";
				process.StandardOutput.Dispose();
				process.StandardError.Dispose();
				_ = pumps.ContinueWith(late => late.Exception, TaskContinuationOptions.OnlyOnFaulted);
			}
			await stopFlushing.CancelAsync();
			await flushing;
			Flush(job);
			code = process.ExitCode;
			bool commit;
			lock (job.Sync)
			{
				commit = code == 0 && !job.Cancelled;
				job.Committing = commit;
			}
			if (commit) outputs = Place(job);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			error = failure.Message;
			outputs = [];
		}
		catch (Exception failure)
		{
			_onError("engine job", failure);
			error = failure.Message;
			outputs = [];
		}
		finally
		{
			Finish(job);
		}
		// A cancel that arrived while the file was being placed was too late: the file stands, and the exit says it wasn't cancelled.
		bool cancelled;
		lock (job.Sync) cancelled = job.Cancelled && !job.Committing;
		var exit = new JobExit(job.Id, code, cancelled, outputs, job.StderrTail(), error);
		try
		{
			job.OnExit(exit);
		}
		catch (Exception failure)
		{
			_onError("engine exit", failure);
		}
	}

	/// <summary>Frees the slot, the engine and the folder before the exit event, so whatever the event triggers (a new job, a Remove) finds them free.</summary>
	private void Finish(Job job)
	{
		lock (_gate)
		{
			_jobs.Remove(job.Id);
			_ended[job.Id] = job.Owner.Window;
			_endedOrder.Enqueue(job.Id);
			while (_endedOrder.Count > RememberedEnded) _ended.Remove(_endedOrder.Dequeue());
		}
		job.Process?.Dispose();
		job.Tree.Dispose();
		job.Lease.Dispose();
		Discard(job.Folder);
	}

	private void Abandon(Job? job, JobObject? tree, IDisposable lease, string folder, string id)
	{
		lock (_gate) _jobs.Remove(id);
		job?.Process?.Dispose();
		tree?.Dispose();
		lease.Dispose();
		Discard(folder);
	}

	private async Task FlushLoopAsync(Job job, CancellationToken stop)
	{
		using var timer = new PeriodicTimer(_flushEvery);
		try
		{
			while (await timer.WaitForNextTickAsync(stop)) Flush(job);
		}
		catch (OperationCanceledException)
		{
			// That is the stop asked for once the pipes have drained.
		}
	}

	private void Flush(Job job)
	{
		var batch = job.TakeBatch();
		if (batch is null) return;
		try
		{
			job.OnOutput(batch);
		}
		catch (Exception failure)
		{
			_onError("engine output", failure);
		}
	}

	/// <summary>Reads lines, treating \r as a line end too (FFmpeg redraws its progress with it), and cutting any line at <see cref="MaxLineChars"/> so a stream with no line ends can't grow in memory.</summary>
	private static async Task PumpAsync(Job job, StreamReader reader, string stream)
	{
		var buffer = new char[4096];
		var line = new StringBuilder();
		var cut = false;
		var afterReturn = false;
		void End()
		{
			if (line.Length > 0) job.Add(stream, cut ? line + "…" : line.ToString());
			line.Clear();
			cut = false;
		}
		int read;
		while ((read = await reader.ReadAsync(buffer)) > 0)
		{
			for (var i = 0; i < read; i++)
			{
				var ch = buffer[i];
				if (ch == '\n' && afterReturn)
				{
					afterReturn = false;
					continue;
				}
				afterReturn = ch == '\r';
				if (ch is '\r' or '\n')
				{
					End();
					continue;
				}
				if (line.Length < MaxLineChars) line.Append(ch);
				else cut = true;
			}
		}
		End();
	}

	/// <summary>Places every output, or none: one that is missing or can't be placed undoes the rest.</summary>
	private List<string> Place(Job job)
	{
		for (var i = 0; i < job.Names.Count; i++)
		{
			if (!File.Exists(job.TempOutputs[i])) throw new EngineOutputMissingException(job.Names[i]);
		}
		Directory.CreateDirectory(job.OutputDirectory);
		List<string> placed = [];
		try
		{
			for (var i = 0; i < job.Names.Count; i++) placed.Add(MoveIntoPlace(job, job.TempOutputs[i], job.Names[i]));
		}
		catch
		{
			foreach (var path in placed) DeleteFile(path, "undo");
			throw;
		}
		return placed;
	}

	/// <summary>
	/// On one volume the move is a rename: whole or not at all. Across volumes a move is a copy that a crash could leave
	/// half-made under the real name, so the copy goes to a hidden part file beside the final name (created new, never
	/// over anything) and is renamed from there. The part file of a Zing that crashed meanwhile stays: it sits in the
	/// person's folder, which Zing can't safely sweep.
	/// </summary>
	private string MoveIntoPlace(Job job, string temp, string name)
	{
		var staged = SameVolume(temp, job.OutputDirectory) ? null : Stage(job, temp, name);
		try
		{
			return _placement.Place(staged ?? temp, job.OutputDirectory, name, () => BeforeMove?.Invoke(job.Id));
		}
		finally
		{
			if (staged is not null) DeleteFile(staged, "remove part file");
		}
	}

	private static string StagedName(Job job, string name) => $".{name}.{job.Id[..8]}.zing-part";

	private string Stage(Job job, string temp, string name)
	{
		var part = Path.Combine(job.OutputDirectory, StagedName(job, name));
		try
		{
			using var source = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var destination = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			source.CopyTo(destination);
		}
		catch
		{
			DeleteFile(part, "remove part file");
			throw;
		}
		return part;
	}

	private void DeleteFile(string path, string what)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			_onError($"{what} {path}", failure);
		}
	}

	private void Discard(string folder)
	{
		if (!Directory.Exists(folder)) return;
		try
		{
			Directory.Delete(folder, recursive: true);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			_onError($"remove {folder}", failure);
		}
	}

	// Zing runs one instance per profile (SingleInstance), so no job of another Zing can be using this folder.
	private void RemoveLeftovers()
	{
		if (!Directory.Exists(_root)) return;
		foreach (var folder in Directory.GetDirectories(_root)) Discard(folder);
	}

	private sealed class Job(string id, JobOwner owner, string folder, string outputDirectory, IReadOnlyList<string> names, IDisposable lease, JobObject tree, Action<JobOutputBatch> onOutput, Action<JobExit> onExit)
	{
		private readonly List<JobOutput> _pending = [];
		private readonly Queue<string> _stderr = new();
		private int _skipped;

		public string Id { get; } = id;
		public JobOwner Owner { get; } = owner;
		public string Folder { get; } = folder;
		public string WorkFolder { get; } = Path.Combine(folder, "work");
		public string TempFolder { get; } = Path.Combine(folder, "tmp");
		public string MagickFolder { get; } = Path.Combine(folder, "magick");
		public string OutputDirectory { get; } = outputDirectory;
		public IReadOnlyList<string> Names { get; } = names;
		public IReadOnlyList<string> TempOutputs { get; } = [.. names.Select((name, i) => Path.Combine(folder, "out", i.ToString(), name))];
		public IDisposable Lease { get; } = lease;
		public Action<JobOutputBatch> OnOutput { get; } = onOutput;
		public Action<JobExit> OnExit { get; } = onExit;
		public object Sync { get; } = new();
		public bool Cancelled { get; set; }
		public bool Committing { get; set; }
		public Process? Process { get; set; }
		public JobObject Tree { get; } = tree;

		public void Add(string stream, string text)
		{
			lock (Sync)
			{
				// Progress is the newest line, so a flood drops the oldest and says how many.
				if (_pending.Count >= MaxBatchLines)
				{
					_pending.RemoveAt(0);
					_skipped++;
				}
				_pending.Add(new JobOutput(stream, text));
				if (stream != "err") return;
				_stderr.Enqueue(text);
				while (_stderr.Count > StderrTailLines) _stderr.Dequeue();
			}
		}

		public JobOutputBatch? TakeBatch()
		{
			lock (Sync)
			{
				if (_pending.Count == 0 && _skipped == 0) return null;
				var batch = new JobOutputBatch(Id, [.. _pending], _skipped);
				_pending.Clear();
				_skipped = 0;
				return batch;
			}
		}

		public IReadOnlyList<string> StderrTail()
		{
			lock (Sync) return [.. _stderr];
		}
	}
}

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
/// <param name="OutputNames">The file names the job will make, in order. Each is placed under this name, numbered ("clip 2.mp4") if it is taken.</param>
/// <param name="Urls">Addresses for tools like yt-dlp; appended after a "--".</param>
public sealed record EngineJobRequest(string EngineId, IReadOnlyList<JobArg> Args, string OutputDirectory, IReadOnlyList<string> OutputNames, IReadOnlyList<string> Urls);

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
/// arguments go through ArgumentList, never a shell; the process starts with no console window, an empty private
/// folder as its working directory (so a program or DLL planted beside someone's input isn't picked up from it) and
/// no stdin. Each job has a Win32 job object, so it ends with Zing, and cancelling ends the whole process tree.
/// An engine writes to a temp file in the job's folder and the file moves into place only on exit code 0 and no
/// cancel, never over an existing file; the job's folder is then deleted. Nothing is logged or kept on disk.
/// </summary>
public sealed class EngineRunner
{
	public const int MaxRunning = 3;
	public const int MaxBatchLines = 200;
	public const int MaxLineChars = 2000;
	public const int StderrTailLines = 20;
	private const int RememberedEnded = 64;
	private const int MaxNumbering = 1000;

	private readonly EngineStore _store;
	private readonly string _root;
	private readonly Action<string, Exception> _onError;
	private readonly TimeSpan _flushEvery;
	private readonly object _gate = new();
	private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _ended = new(StringComparer.Ordinal);
	private readonly Queue<string> _endedOrder = new();

	/// <param name="root">Where jobs' private folders go, under Zing's data. Anything left in it from a Zing that was killed is deleted now.</param>
	/// <param name="onError">Called for a failure nobody awaits: a folder that couldn't be deleted (tried again at the next start) or a callback that threw.</param>
	public EngineRunner(EngineStore store, string root, Action<string, Exception> onError, TimeSpan? flushEvery = null)
	{
		_store = store;
		_root = root;
		_onError = onError;
		_flushEvery = flushEvery ?? TimeSpan.FromMilliseconds(100);
		RemoveLeftovers();
	}

	public int Running
	{
		get
		{
			lock (_gate) return _jobs.Count;
		}
	}

	/// <summary>Test seam: runs before each attempt to move an output into place, with the job's id.</summary>
	internal Action<string>? BeforeMove { get; set; }

	/// <summary>
	/// Starts a job and returns its id at once; <paramref name="onOutput"/> and then, exactly once, <paramref name="onExit"/>
	/// follow on another thread. The exit comes after every line of output and after the outputs are in place.
	/// </summary>
	/// <exception cref="UnknownEngineException">Not an id in the catalog.</exception>
	/// <exception cref="EngineNotInstalledException">Not downloaded; running never downloads.</exception>
	/// <exception cref="UnsafeEngineArgumentException">A literal that names a file (a rooted path, a backslash, "..", a file: address), a bad output name or URL.</exception>
	/// <exception cref="TooManyJobsException"><see cref="MaxRunning"/> jobs are already running.</exception>
	public string Start(JobOwner owner, EngineJobRequest request, Action<JobOutputBatch> onOutput, Action<JobExit> onExit)
	{
		var names = request.OutputNames.Select(SafeOutputName).ToList();
		foreach (var arg in request.Args) Validate(arg, names.Count);
		var urls = request.Urls.Count == 0 ? [] : EngineArguments.Urls(request.Urls);
		var lease = _store.Use(request.EngineId);
		var id = Guid.NewGuid().ToString("N");
		var folder = Path.Combine(_root, id);
		Job? job = null;
		try
		{
			var executable = _store.InstalledPath(request.EngineId);
			job = new Job(id, owner, folder, request.OutputDirectory, names, lease, onOutput, onExit);
			lock (_gate)
			{
				if (_jobs.Count >= MaxRunning) throw new TooManyJobsException(MaxRunning);
				_jobs[id] = job;
			}
			Directory.CreateDirectory(job.WorkFolder);
			for (var i = 0; i < names.Count; i++) Directory.CreateDirectory(Path.GetDirectoryName(job.TempOutputs[i])!);
			var process = StartProcess(job, executable, request, urls);
			job.Process = process;
		}
		catch
		{
			Abandon(job, lease, folder, id);
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
			case JobArg.Literal literal:
				ValidateLiteral(literal.Text);
				break;
			case JobArg.Input input:
				EngineArguments.FilePath(input.Path);
				break;
			case JobArg.Output output when output.Index < 0 || output.Index >= outputs:
				throw new UnsafeEngineArgumentException($"Output {output.Index} isn't one the job named ({outputs} named)");
		}
	}

	/// <summary>
	/// Option text may not name a file: the renderer gets files only as tokens, and a path typed as option text would
	/// slip past that. This catches the ways to say one in Windows (a rooted path, "C:x", a UNC or backslash path, "..",
	/// a file: address). It can't stop an engine's own mini-languages (an FFmpeg filter) from reading a file, so the tools
	/// build their option text themselves and never pass a person's text through as an option.
	/// </summary>
	private static void ValidateLiteral(string text)
	{
		var segments = text.Split('/', '\\');
		var namesFile = Path.IsPathRooted(text) || text.Contains('\\') || segments.Contains("..") || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
		if (namesFile) throw new UnsafeEngineArgumentException($"{text} looks like a file path, which an engine may only be given as a file the person picked or dropped");
	}

	private static string SafeOutputName(string name)
	{
		var plain = name.Length > 0 && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.TrimEnd('.', ' ').Length > 0;
		if (!plain) throw new UnsafeEngineArgumentException($"\"{name}\" isn't a plain file name");
		return DownloadNames.FileName(name, _ => false);
	}

	private Process StartProcess(Job job, string executable, EngineJobRequest request, IReadOnlyList<string> urls)
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
		foreach (var arg in request.Args)
		{
			start.ArgumentList.Add(arg switch
			{
				JobArg.Literal literal => literal.Text,
				JobArg.Input input => EngineArguments.FilePath(input.Path),
				JobArg.Output output => job.TempOutputs[output.Index],
				_ => throw new InvalidOperationException($"Unknown job argument {arg}"),
			});
		}
		foreach (var url in urls) start.ArgumentList.Add(url);
		job.Tree = JobObject.Create();
		var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} didn't start");
		try
		{
			// .NET can't start a process suspended, so it runs for the instant before this call. A child it started in that
			// instant would not be in the job: an engine takes far longer than that to start one (the runtime loads first).
			job.Tree.Assign(process);
		}
		catch
		{
			process.Kill(entireProcessTree: true);
			process.Dispose();
			throw;
		}
		process.StandardInput.Close();
		return process;
	}

	private async Task RunAsync(Job job)
	{
		var outputs = new List<string>();
		string? error = null;
		var code = -1;
		var cancelled = false;
		try
		{
			var process = job.Process!;
			using var stopFlushing = new CancellationTokenSource();
			var flushing = FlushLoopAsync(job, stopFlushing.Token);
			var pumps = Task.WhenAll(PumpAsync(job, process.StandardOutput, "out"), PumpAsync(job, process.StandardError, "err"));
			await process.WaitForExitAsync();
			// Anything the engine left running holds the pipes open and would keep the output from draining; none should outlive it.
			job.Tree.Terminate();
			await pumps;
			await stopFlushing.CancelAsync();
			await flushing;
			Flush(job);
			code = process.ExitCode;
			bool commit;
			lock (job.Sync)
			{
				commit = code == 0 && !job.Cancelled;
				job.Committing = commit;
				cancelled = job.Cancelled && !commit;
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
		job.Tree?.Dispose();
		job.Lease.Dispose();
		Discard(job.Folder);
	}

	private void Abandon(Job? job, IDisposable lease, string folder, string id)
	{
		lock (_gate) _jobs.Remove(id);
		job?.Process?.Dispose();
		job?.Tree?.Dispose();
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

	/// <summary>Moves every output out of the job's folder, or none: one that is missing or can't be placed undoes the rest.</summary>
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
			foreach (var path in placed)
			{
				try
				{
					File.Delete(path);
				}
				catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
				{
					_onError($"undo {path}", undo);
				}
			}
			throw;
		}
		return placed;
	}

	/// <summary>The name is chosen against what exists, and File.Move refuses to overwrite, so a file that arrives in between makes the next number be tried.</summary>
	private string MoveIntoPlace(Job job, string temp, string name)
	{
		for (var attempt = 0; attempt < MaxNumbering; attempt++)
		{
			var target = Path.Combine(job.OutputDirectory, DownloadNames.FileName(name, candidate => Taken(Path.Combine(job.OutputDirectory, candidate))));
			BeforeMove?.Invoke(job.Id);
			try
			{
				File.Move(temp, target, overwrite: false);
				return target;
			}
			catch (IOException) when (Taken(target))
			{
				// Taken in the instant since it was checked; choose again.
			}
		}
		throw new IOException($"Couldn't find a free name for {name} in {job.OutputDirectory}");
	}

	private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path);

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

	private sealed class Job(string id, JobOwner owner, string folder, string outputDirectory, IReadOnlyList<string> names, IDisposable lease, Action<JobOutputBatch> onOutput, Action<JobExit> onExit)
	{
		private readonly List<JobOutput> _pending = [];
		private readonly Queue<string> _stderr = new();
		private int _skipped;

		public string Id { get; } = id;
		public JobOwner Owner { get; } = owner;
		public string Folder { get; } = folder;
		public string WorkFolder { get; } = Path.Combine(folder, "work");
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
		public JobObject Tree { get; set; } = null!;

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

using System.Diagnostics;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

/// <summary>Zing.FakeEngine installed as one engine ("ffmpeg" unless told), and a runner over it with its folders under a scratch root.</summary>
public sealed class RunnerFixture : IDisposable
{
	private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
	private readonly EngineFixture _engines = new();
	private readonly object _gate = new();
	private bool _installed;

	/// <summary>The flags the fake engine understands, which the real catalog's allow-list naturally lacks.</summary>
	internal static readonly Dictionary<string, FlagSpec> FakeFlags = new(StringComparer.Ordinal)
	{
		["--fake-write"] = new FlagSpec(FlagValue.Output),
		["--write-after"] = new FlagSpec(FlagValue.Number, FlagValue.Output),
		["--print-cwd"] = new FlagSpec(),
		["--spawn-child"] = new FlagSpec(),
		["--sleep"] = new FlagSpec(FlagValue.Number),
		["--chatty"] = new FlagSpec(FlagValue.Number),
		["--long-line"] = new FlagSpec(FlagValue.Number),
		["--err"] = new FlagSpec(FlagValue.Unchecked),
		["--exit"] = new FlagSpec(FlagValue.Number),
		["--print-env"] = new FlagSpec(),
		["--print-magick-policy"] = new FlagSpec(),
	};

	public RunnerFixture(TimeSpan? flushEvery = null, string engineId = "ffmpeg")
	{
		EngineId = engineId;
		Jobs = Path.Combine(_engines.Root, "jobs");
		Outputs = Path.Combine(_engines.Root, "outputs");
		Inputs = Path.Combine(_engines.Root, "inputs");
		Directory.CreateDirectory(Outputs);
		Directory.CreateDirectory(Inputs);
		var body = System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Zing.FakeEngine.exe"));
		_engines.Serve($"{engineId}/{engineId}.exe", body);
		Store = _engines.Store(_engines.SingleFile(engineId, body));
		var policy = EngineFlagPolicy.Pinned.Plus(engineId, FakeFlags, "txt");
		Runner = new EngineRunner(Store, Jobs, (where, error) => Errors.Add((where, error)), policy, flushEvery ?? TimeSpan.FromMilliseconds(20));
	}

	public string EngineId { get; }

	public EngineStore Store { get; }

	public EngineRunner Runner { get; }

	public string Jobs { get; }

	public string Outputs { get; }

	public string Inputs { get; }

	public List<(string Where, Exception Error)> Errors { get; } = [];

	/// <summary>Every callback the runner made, in order: "out:&lt;text&gt;", "err:&lt;text&gt;" and "exit".</summary>
	public List<string> Events { get; } = [];

	public List<JobOutputBatch> Batches { get; } = [];

	public List<JobExit> Exits { get; } = [];

	public JobOwner Owner { get; } = new("window-1", "panel-1");

	public async Task InstallAsync()
	{
		if (_installed) return;
		var path = await Store.EnsureAsync(EngineId, new InlineProgress<double>(_ => { }), CancellationToken.None);
		// The apphost needs its dll and runtime config beside it.
		foreach (var sibling in new[] { "Zing.FakeEngine.dll", "Zing.FakeEngine.runtimeconfig.json" })
		{
			System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, sibling), Path.Combine(Path.GetDirectoryName(path)!, sibling));
		}
		_installed = true;
	}

	public EngineJobRequest Request(params JobArg[] args) => new(EngineId, args, Outputs, [], []);

	public EngineJobRequest Request(IReadOnlyList<string> outputNames, params JobArg[] args) => new(EngineId, args, Outputs, outputNames, []);

	public static JobArg[] Literals(params string[] texts) => [.. texts.Select(t => (JobArg)new JobArg.Literal(t))];

	public string Start(EngineJobRequest request, JobOwner? owner = null) =>
		Runner.Start(owner ?? Owner, request, batch =>
		{
			lock (_gate)
			{
				Batches.Add(batch);
				foreach (var line in batch.Lines) Events.Add($"{line.Stream}:{line.Text}");
			}
		}, exit =>
		{
			lock (_gate)
			{
				Exits.Add(exit);
				Events.Add("exit");
			}
		});

	public async Task<JobExit> RunAsync(EngineJobRequest request)
	{
		var before = Exits.Count;
		Start(request);
		return await ExitAsync(before + 1);
	}

	public async Task<JobExit> ExitAsync(int count)
	{
		await Until(() => Exits.Count >= count, "the job to exit");
		lock (_gate) return Exits[count - 1];
	}

	public async Task Until(Func<bool> condition, string what)
	{
		var clock = Stopwatch.StartNew();
		while (clock.Elapsed < Wait)
		{
			lock (_gate)
			{
				if (condition()) return;
			}
			await Task.Delay(10);
		}
		throw new TimeoutException($"Timed out waiting for {what}. Events: {string.Join(" | ", Events)}");
	}

	public IReadOnlyList<string> Lines(string stream)
	{
		lock (_gate) return [.. Batches.SelectMany(b => b.Lines).Where(l => l.Stream == stream).Select(l => l.Text)];
	}

	/// <summary>What is in a folder now, by name.</summary>
	public string[] Entries(string folder) => Directory.Exists(folder) ? [.. Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Cast<string>().Order()] : [];

	public string Make(string name, string content = "input")
	{
		var path = Path.Combine(Inputs, name);
		System.IO.File.WriteAllText(path, content);
		return path;
	}

	public void Dispose()
	{
		// Anything a failed test left running is stopped before the scratch folder goes.
		Runner.KillWindow(Owner.Window);
		var clock = Stopwatch.StartNew();
		while (Runner.Running > 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
		_engines.Dispose();
	}
}

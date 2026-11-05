using System.Text;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

/// <summary>
/// Local only, never in CI: the real yt-dlp and FFmpeg from the pinned addresses, run through the real runner with the
/// Download tool's MP3 arguments (src/shared/convert/ytdlpArgs.ts). Skipped unless ZING_REAL_ENGINES=1. It makes one
/// request to the address in ZING_REAL_URL (use a video you may fetch), and keeps the engines in ZING_REAL_ENGINES_DIR,
/// which must be outside the repo. Writes real-run.log there. A site's bot check is a finding, not something to get past.
/// </summary>
public sealed class RealEngineTests
{
	private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set {name}");

	[Fact]
	public async Task The_real_yt_dlp_and_FFmpeg_make_an_mp3_through_the_runner()
	{
		Assert.SkipUnless(Environment.GetEnvironmentVariable("ZING_REAL_ENGINES") == "1", "Real engines run only by hand: set ZING_REAL_ENGINES=1");
		var root = Required("ZING_REAL_ENGINES_DIR");
		var url = Required("ZING_REAL_URL");
		var log = new StringBuilder();
		var errors = new List<string>();
		void OnError(string where, Exception error) => errors.Add($"{where}: {error}");
		using var http = new HttpClient();
		var store = new EngineStore(Path.Combine(root, "engines"), EngineCatalog.Pinned, http, OnError);
		foreach (var id in new[] { EngineIds.YtDlp, EngineIds.Ffmpeg })
		{
			if (store.List().Single(e => e.Engine.Id == id).InstalledVersion is not null) continue;
			await store.EnsureAsync(id, new InlineProgress<double>(_ => { }), CancellationToken.None);
		}

		var outputs = Path.Combine(root, "outputs");
		var runner = new EngineRunner(store, Path.Combine(root, "jobs"), OnError);
		var exit = new TaskCompletionSource<JobExit>();
		JobArg[] args = [new JobArg.Literal("--newline"), new JobArg.Literal("--no-playlist"), new JobArg.Literal("-x"), new JobArg.Literal("--audio-format"), new JobArg.Literal("mp3"), new JobArg.Literal("--audio-quality"), new JobArg.Literal("0"), new JobArg.Literal("-o"), new JobArg.Output(0)];
		runner.Start(
			new JobOwner("real", "panel"),
			new EngineJobRequest(EngineIds.YtDlp, args, outputs, ["audio.mp3"], [url]),
			batch =>
			{
				lock (log)
				{
					foreach (var line in batch.Lines) log.AppendLine($"{line.Stream}: {line.Text}");
				}
			},
			result => exit.TrySetResult(result));
		var done = await exit.Task.WaitAsync(TimeSpan.FromMinutes(4), TestContext.Current.CancellationToken);

		lock (log)
		{
			log.AppendLine($"exit code {done.Code}, cancelled {done.Cancelled}, error {done.Error}, outputs {string.Join(",", done.Outputs)}");
			foreach (var error in errors) log.AppendLine($"runner error: {error}");
			File.WriteAllText(Path.Combine(root, "real-run.log"), log.ToString());
		}
		Assert.True(done.Code == 0 && done.Outputs.Count == 1, $"See {Path.Combine(root, "real-run.log")}: exit {done.Code}, {string.Join(" | ", done.StderrTail)}");
	}
}

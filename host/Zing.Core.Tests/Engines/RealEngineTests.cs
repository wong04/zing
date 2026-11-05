using System.Diagnostics;
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

	private static void SkipUnlessAsked(string mode) =>
		Assert.SkipUnless(Environment.GetEnvironmentVariable("ZING_REAL_ENGINES") == "1" && Environment.GetEnvironmentVariable("ZING_REAL_MODE") == mode, $"Real engines run only by hand: set ZING_REAL_ENGINES=1 and ZING_REAL_MODE={mode}");

	[Fact]
	public async Task The_real_yt_dlp_and_FFmpeg_make_an_mp3_through_the_runner()
	{
		SkipUnlessAsked("mp3");
		await RunAsync("audio.mp3", [new JobArg.Literal("-x"), new JobArg.Literal("--audio-format"), new JobArg.Literal("mp3"), new JobArg.Literal("--audio-quality"), new JobArg.Literal("0")]);
	}

	/// <summary>The arguments of ytdlpJob(url, 'mp4'), and a check that the file is MP4 with both streams.</summary>
	[Fact]
	public async Task The_real_yt_dlp_and_FFmpeg_make_a_playable_mp4_with_audio_and_video_through_the_runner()
	{
		SkipUnlessAsked("mp4");
		var placed = await RunAsync("video.mp4", [new JobArg.Literal("-f"), new JobArg.Literal("bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b"), new JobArg.Literal("--merge-output-format"), new JobArg.Literal("mp4")]);

		var head = File.ReadAllBytes(placed)[..12];
		Assert.Equal("ftyp", Encoding.ASCII.GetString(head[4..8]));
		var probe = Directory.GetFiles(Path.Combine(Required("ZING_REAL_ENGINES_DIR"), "engines"), "ffprobe.exe", SearchOption.AllDirectories).Single();
		using var process = Process.Start(new ProcessStartInfo(probe, ["-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "csv=p=0", placed]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
		var streams = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(Required("ZING_REAL_ENGINES_DIR"), "real-run-mp4-probe.log"), $"ftyp ok{Environment.NewLine}{streams}{Environment.NewLine}ffprobe exit {process.ExitCode} {errors}");
		Assert.Equal(0, process.ExitCode);
		Assert.Contains("video", streams);
		Assert.Contains("audio", streams);
	}

	private static async Task<string> RunAsync(string outputName, JobArg[] modeArgs)
	{
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
		JobArg[] args = [new JobArg.Literal("--newline"), new JobArg.Literal("--no-playlist"), .. modeArgs, new JobArg.Literal("-o"), new JobArg.Output(0)];
		runner.Start(
			new JobOwner("real", "panel"),
			new EngineJobRequest(EngineIds.YtDlp, args, outputs, [outputName], [url]),
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
			File.WriteAllText(Path.Combine(root, $"real-run-{outputName}.log"), log.ToString());
		}
		Assert.True(done.Code == 0 && done.Outputs.Count == 1, $"See {Path.Combine(root, $"real-run-{outputName}.log")}: exit {done.Code}, {string.Join(" | ", done.StderrTail)}");
		return done.Outputs[0];
	}
}

using System.Diagnostics;
using System.Text.Json;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

/// <summary>What the runner puts around an engine: safety flags, a scrubbed environment, staging, and the order of start and cancel.</summary>
[Collection("ProcessWide")]
public sealed class EngineRunnerSafetyTests
{
	private static JobArg Literal(string text) => new JobArg.Literal(text);

	private static JobArg Output(int index) => new JobArg.Output(index);

	[Theory]
	[InlineData("pandoc", new[] { "--sandbox" })]
	[InlineData("ffmpeg", new[] { "-nostdin" })]
	public async Task Each_engine_gets_its_safety_flags_first_and_they_cannot_be_left_out(string engine, string[] flags)
	{
		using var f = new RunnerFixture(engineId: engine);
		await f.InstallAsync();

		await f.RunAsync(f.Request(Literal("--exit"), Literal("0")));

		var echoed = JsonSerializer.Deserialize<string[]>(f.Lines("out")[0])!;
		Assert.Equal([.. flags, "--exit", "0"], echoed);
	}

	[Fact]
	public async Task Yt_dlp_gets_its_safety_flags_first_and_then_FFmpegs_location_from_the_host()
	{
		using var f = new RunnerFixture(engineId: "ytdlp");
		await f.InstallAsync();

		await f.RunAsync(f.Request(Literal("--exit"), Literal("0")));

		var echoed = JsonSerializer.Deserialize<string[]>(f.Lines("out")[0])!;
		Assert.Equal(["--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--ffmpeg-location", f.Store.InstalledPath("ffmpeg"), "--exit", "0"], echoed);
		Assert.True(File.Exists(echoed[5]));
	}

	[Fact]
	public async Task Yt_dlp_never_fetches_a_playlist_whatever_the_renderer_sent()
	{
		using var f = new RunnerFixture(engineId: "ytdlp");
		await f.InstallAsync();

		await f.RunAsync(f.Request(Literal("--exit"), Literal("0")) with { Urls = ["https://example.com/watch?v=1&list=PL1"] });

		Assert.Contains("--no-playlist", JsonSerializer.Deserialize<string[]>(f.Lines("out")[0])!);
		Assert.Contains("--no-playlist", EngineFlagPolicy.Pinned.For("ytdlp").SafetyFirst);
	}

	[Fact]
	public async Task A_renderer_cannot_name_FFmpegs_location_itself()
	{
		using var f = new RunnerFixture(engineId: "ytdlp");
		await f.InstallAsync();

		Assert.Throws<DisallowedEngineFlagException>(() => f.Start(f.Request(Literal("--ffmpeg-location"), Literal(@"C:\evil"))));
	}

	[Fact]
	public async Task Yt_dlp_will_not_start_without_FFmpeg_downloaded_and_nothing_is_left_behind()
	{
		using var f = new RunnerFixture(engineId: "ytdlp");
		await f.InstallAsync();
		f.Store.Remove("ffmpeg");

		var error = Assert.Throws<EngineNotInstalledException>(() => f.Start(f.Request(Literal("--exit"), Literal("0"))));

		Assert.Contains("FFmpeg", error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(0, f.Runner.Running);
		Assert.Empty(f.Entries(f.Jobs));
		// The yt-dlp lease taken for the attempt is released again.
		f.Store.Remove("ytdlp");
	}

	[Fact]
	public async Task FFmpeg_cannot_be_removed_while_a_yt_dlp_job_uses_it()
	{
		using var f = new RunnerFixture(engineId: "ytdlp");
		await f.InstallAsync();
		f.Start(f.Request(Literal("--sleep"), Literal("60000")));

		Assert.Throws<EngineInUseException>(() => f.Store.Remove("ffmpeg"));
		Assert.Throws<EngineInUseException>(() => f.Store.Remove("ytdlp"));

		f.Runner.KillWindow(f.Owner.Window);
		await f.ExitAsync(1);
		f.Store.Remove("ffmpeg");
	}

	[Fact]
	public async Task The_environment_is_scrubbed_to_a_few_variables_of_the_jobs_own()
	{
		Environment.SetEnvironmentVariable("ZING_TEST_SECRET", "hunter2");
		Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://proxy.example:8080");
		try
		{
			using var f = new RunnerFixture();
			await f.InstallAsync();

			await f.RunAsync(f.Request(Literal("--print-env")));

			var env = EnvOf(f);
			Assert.False(env.ContainsKey("ZING_TEST_SECRET"));
			Assert.False(env.ContainsKey("HTTPS_PROXY"));
			Assert.All(env.Keys, key => Assert.Contains(key, new[] { "SystemRoot", "windir", "TEMP", "TMP", "PATH" }));
			Assert.StartsWith(f.Jobs + Path.DirectorySeparatorChar, env["TEMP"]);
			Assert.Equal(env["TEMP"], env["TMP"]);
			Assert.Equal(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), env["SystemRoot"]);
			// Only the engine's own folder: nothing the person put on PATH.
			Assert.Equal(Path.GetDirectoryName(f.Store.InstalledPath("ffmpeg")), env["PATH"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ZING_TEST_SECRET", null);
			Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
		}
	}

	[Fact]
	public async Task Only_yt_dlp_gets_the_proxy_variables()
	{
		Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://proxy.example:8080");
		Environment.SetEnvironmentVariable("ZING_TEST_SECRET", "hunter2");
		try
		{
			using var f = new RunnerFixture(engineId: "ytdlp");
			await f.InstallAsync();

			await f.RunAsync(f.Request(Literal("--print-env")));

			var env = EnvOf(f);
			Assert.Equal("http://proxy.example:8080", env["HTTPS_PROXY"]);
			Assert.False(env.ContainsKey("ZING_TEST_SECRET"));
		}
		finally
		{
			Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
			Environment.SetEnvironmentVariable("ZING_TEST_SECRET", null);
		}
	}

	[Fact]
	public async Task ImageMagick_is_given_its_output_without_the_percent_and_bracket_characters_of_the_jobs_folder()
	{
		// ImageMagick expands "%d" and reads "[0]" in the file name it writes (checked with the real engine), so a Zing
		// folder with them in its path would break every conversion. Its output is given relative to the job's working folder.
		using var f = new RunnerFixture(engineId: "magick", jobsFolder: "100%d [0] jobs");
		await f.InstallAsync();

		var exit = await f.RunAsync(f.Request(["o.txt"], Literal("--fake-write"), new JobArg.Output(0)));

		Assert.Equal(0, exit.Code);
		Assert.Equal(["o.txt"], exit.Outputs.Select(Path.GetFileName));
		var echoed = System.Text.Json.JsonSerializer.Deserialize<string[]>(f.Lines("out")[0])!;
		Assert.DoesNotContain('%', echoed[1]);
		Assert.DoesNotContain('[', echoed[1]);
	}

	[Fact]
	public async Task ImageMagick_reads_a_restrictive_policy_the_job_wrote()
	{
		using var f = new RunnerFixture(engineId: "magick");
		await f.InstallAsync();

		await f.RunAsync(f.Request(Literal("--print-env"), Literal("--print-magick-policy")));

		var env = EnvOf(f);
		Assert.StartsWith(f.Jobs + Path.DirectorySeparatorChar, env["MAGICK_CONFIGURE_PATH"]);
		var policy = string.Join("\n", f.Lines("out").SkipWhile(l => !l.StartsWith("<?xml") && !l.StartsWith("<policymap")));
		Assert.Contains("MSL", policy);
		Assert.Contains("@*", policy);
		Assert.Contains("PDF", policy);
	}

	[Fact]
	public async Task Other_engines_get_no_magick_variable()
	{
		using var f = new RunnerFixture();
		await f.InstallAsync();

		await f.RunAsync(f.Request(Literal("--print-env")));

		Assert.False(EnvOf(f).ContainsKey("MAGICK_CONFIGURE_PATH"));
	}

	private static Dictionary<string, string> EnvOf(RunnerFixture f)
	{
		var line = f.Lines("out").Single(l => l.StartsWith("env "));
		return JsonSerializer.Deserialize<Dictionary<string, string>>(line["env ".Length..])!;
	}

	[Fact]
	public async Task A_close_between_the_process_starting_and_joining_its_job_still_kills_it()
	{
		using var f = new RunnerFixture();
		await f.InstallAsync();
		f.Runner.AfterStart = _ => f.Runner.KillWindow(f.Owner.Window);

		f.Start(f.Request(Literal("--sleep"), Literal("60000")));
		var exit = await f.ExitAsync(1);

		Assert.True(exit.Cancelled);
		await f.Until(() => f.Runner.Running == 0, "the job to end");
	}

	[Fact]
	public async Task A_close_before_the_job_object_is_ready_cannot_find_a_half_built_job()
	{
		using var f = new RunnerFixture();
		await f.InstallAsync();
		var started = new ManualResetEventSlim();
		var closer = Task.Run(() =>
		{
			started.Wait();
			for (var i = 0; i < 2000; i++) f.Runner.KillWindow(f.Owner.Window);
		}, TestContext.Current.CancellationToken);

		for (var i = 0; i < 20; i++)
		{
			started.Set();
			f.Start(f.Request(Literal("--exit"), Literal("0")));
			await f.Until(() => f.Runner.Running == 0, "the job to end");
		}

		await closer;
		Assert.Empty(f.Errors);
	}

	[Fact]
	public async Task Without_a_shared_root_the_output_is_staged_beside_its_final_name_then_moved()
	{
		using var f = new RunnerFixture();
		await f.InstallAsync();
		f.Runner.SameVolume = (_, _) => false;
		var seen = new List<string>();
		f.Runner.BeforeMove = _ => seen.AddRange(Directory.GetFiles(f.Outputs).Select(Path.GetFileName)!);

		var exit = await f.RunAsync(f.Request(["clip.txt"], Literal("--fake-write"), Output(0)));

		Assert.Equal([Path.Combine(f.Outputs, "clip.txt")], exit.Outputs);
		Assert.Matches(@"^\.clip\.txt\.[0-9a-f]{8}\.zing-part$", Assert.Single(seen));
		Assert.Equal(["clip.txt"], f.Entries(f.Outputs));
		Assert.Equal("fake engine output\n", File.ReadAllText(exit.Outputs[0]));
	}

	[Fact]
	public async Task A_staged_copy_that_fails_leaves_no_part_file_and_no_output()
	{
		using var f = new RunnerFixture();
		await f.InstallAsync();
		f.Runner.SameVolume = (_, _) => false;
		f.Runner.BeforeMove = _ => throw new IOException("disk full");

		var exit = await f.RunAsync(f.Request(["clip.txt"], Literal("--fake-write"), Output(0)));

		Assert.Empty(exit.Outputs);
		Assert.Contains("disk full", exit.Error);
		Assert.Empty(f.Entries(f.Outputs));
	}

	[Fact]
	public async Task A_staged_copy_never_overwrites_a_file_that_arrives_late()
	{
		using var f = new RunnerFixture();
		await f.InstallAsync();
		f.Runner.SameVolume = (_, _) => false;
		var target = Path.Combine(f.Outputs, "clip.txt");
		f.Runner.BeforeMove = _ =>
		{
			if (!File.Exists(target)) File.WriteAllText(target, "arrived late");
		};

		var exit = await f.RunAsync(f.Request(["clip.txt"], Literal("--fake-write"), Output(0)));

		Assert.Equal("arrived late", File.ReadAllText(target));
		Assert.Equal([Path.Combine(f.Outputs, "clip 2.txt")], exit.Outputs);
		Assert.Equal(["clip 2.txt", "clip.txt"], f.Entries(f.Outputs));
	}

	[Fact]
	public async Task A_pipe_held_open_past_the_engine_does_not_hold_the_exit_forever()
	{
		using var f = new RunnerFixture();
		await f.InstallAsync();
		f.Runner.DrainTimeout = TimeSpan.FromMilliseconds(300);
		f.Runner.DrainBlocker = new TaskCompletionSource().Task;
		var clock = Stopwatch.StartNew();

		var exit = await f.RunAsync(f.Request(Literal("--exit"), Literal("0")));

		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
		Assert.Equal(0, exit.Code);
		Assert.Contains("output", exit.Error);
	}

	[Fact]
	public async Task A_failure_in_the_runner_still_reports_a_cancel_that_happened()
	{
		using var f = new RunnerFixture();
		await f.InstallAsync();
		var blocker = new TaskCompletionSource();
		f.Runner.DrainBlocker = blocker.Task;
		var id = f.Start(f.Request(Literal("--sleep"), Literal("60000")));

		f.Runner.Cancel(id, f.Owner.Window);
		blocker.SetException(new InvalidOperationException("pump broke"));
		var exit = await f.ExitAsync(1);

		Assert.True(exit.Cancelled);
		Assert.Contains("pump broke", exit.Error);
		Assert.Single(f.Errors);
	}
}

using System.Diagnostics;
using System.Text.Json;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

public sealed class EngineRunnerTests : IDisposable
{
	private readonly RunnerFixture _f = new();

	public void Dispose() => _f.Dispose();

	private static JobArg Literal(string text) => new JobArg.Literal(text);

	private static JobArg Input(string path) => new JobArg.Input(path);

	private static JobArg Output(int index) => new JobArg.Output(index);

	/// <summary>True once the process is gone; one that ended before this looked has gone too.</summary>
	private static bool Gone(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return process.WaitForExit(5000);
		}
		catch (ArgumentException)
		{
			return true;
		}
	}

	private static string Text(JobExit exit) => $"code {exit.Code}, cancelled {exit.Cancelled}, error {exit.Error}, outputs {string.Join(",", exit.Outputs)}";

	[Fact]
	public async Task A_file_named_like_an_option_and_an_accented_name_arrive_as_single_unchanged_arguments()
	{
		await _f.InstallAsync();
		var option = _f.Make("-rf.mov");
		var accented = _f.Make("my film é (1).heic");

		var exit = await _f.RunAsync(_f.Request(Literal("-i"), Input(option), Input(accented), Literal("my film é (1).heic")));

		Assert.Equal(0, exit.Code);
		var echoed = JsonSerializer.Deserialize<string[]>(_f.Lines("out")[0])!;
		Assert.Equal(["-i", option, accented, "my film é (1).heic"], echoed);
		// Unchanged is not enough: FFmpeg reads a bare "-rf.mov" as an option, and this one is a path that cannot.
		Assert.All(echoed.Skip(1).Take(2), arg => Assert.False(arg.StartsWith('-')));
	}

	[Fact]
	public async Task Quotes_spaces_and_backslashes_survive_untouched()
	{
		await _f.InstallAsync();
		var awkward = _f.Make("say 'hi' \\ there.txt".Replace("\\", "+"));

		var exit = await _f.RunAsync(_f.Request(Input(awkward), Literal("a b"), Literal("c\"d"), Literal("")));

		Assert.Equal(0, exit.Code);
		Assert.Equal([awkward, "a b", "c\"d", ""], JsonSerializer.Deserialize<string[]>(_f.Lines("out")[0])!);
	}

	[Fact]
	public async Task Urls_come_after_a_double_dash()
	{
		await _f.InstallAsync();

		var request = _f.Request(Literal("--newline")) with { Urls = ["https://example.com/watch?v=1"] };
		await _f.RunAsync(request);

		Assert.Equal(["--newline", "--", "https://example.com/watch?v=1"], JsonSerializer.Deserialize<string[]>(_f.Lines("out")[0])!);
	}

	[Fact]
	public void An_unknown_engine_id_raises()
	{
		var request = _f.Request(Literal("x")) with { EngineId = "notepad" };

		Assert.Throws<UnknownEngineException>(() => _f.Start(request));
	}

	[Fact]
	public void An_engine_that_isnt_downloaded_raises_and_nothing_is_fetched()
	{
		Assert.Throws<EngineNotInstalledException>(() => _f.Start(_f.Request(Literal("x"))));
		Assert.Equal(0, _f.Runner.Running);
	}

	[Theory]
	[InlineData("C:\\Windows\\win.ini")]
	[InlineData("\\\\server\\share\\x")]
	[InlineData("\\x")]
	[InlineData("..\\x")]
	[InlineData("../x")]
	[InlineData("a/../../x")]
	[InlineData("file:///C:/x")]
	[InlineData("FILE:x")]
	[InlineData("C:x")]
	public async Task A_literal_that_names_a_file_the_renderer_was_never_given_is_refused(string literal)
	{
		await _f.InstallAsync();

		Assert.Throws<UnsafeEngineArgumentException>(() => _f.Start(_f.Request(Literal(literal))));
		Assert.Equal(0, _f.Runner.Running);
		Assert.Empty(_f.Entries(_f.Jobs));
	}

	[Theory]
	[InlineData("scale=iw/2:ih/2")]
	[InlineData("-c:v")]
	[InlineData("libx264")]
	[InlineData("-vf")]
	public async Task Ordinary_option_text_is_allowed(string literal)
	{
		await _f.InstallAsync();

		var exit = await _f.RunAsync(_f.Request(Literal(literal)));

		Assert.Equal(0, exit.Code);
	}

	[Theory]
	[InlineData("..\\evil.mp4")]
	[InlineData("sub\\a.mp4")]
	[InlineData("a/b.mp4")]
	[InlineData("C:a.mp4")]
	[InlineData("")]
	[InlineData("a.b/c")]
	public async Task An_output_name_with_a_separator_is_refused(string name)
	{
		await _f.InstallAsync();

		Assert.Throws<UnsafeEngineArgumentException>(() => _f.Start(_f.Request([name], Output(0))));
	}

	[Fact]
	public async Task An_output_that_the_request_never_named_is_refused()
	{
		await _f.InstallAsync();

		Assert.Throws<UnsafeEngineArgumentException>(() => _f.Start(_f.Request(["a.txt"], Output(1))));
	}

	[Fact]
	public async Task The_engine_runs_in_a_private_folder_under_the_jobs_root_that_is_gone_afterwards()
	{
		await _f.InstallAsync();

		var exit = await _f.RunAsync(_f.Request(Literal("--print-cwd")));

		Assert.Equal(0, exit.Code);
		var cwd = _f.Lines("out")[1];
		Assert.StartsWith(_f.Jobs + Path.DirectorySeparatorChar, cwd);
		Assert.NotEqual(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), cwd);
		Assert.False(Directory.Exists(cwd));
		Assert.Empty(_f.Entries(_f.Jobs));
	}

	[Fact]
	public async Task A_finished_job_moves_its_output_into_place_and_reports_the_final_path()
	{
		await _f.InstallAsync();

		var exit = await _f.RunAsync(_f.Request(["clip.txt"], Literal("--fake-write"), Output(0)));

		var expected = Path.Combine(_f.Outputs, "clip.txt");
		Assert.Equal([expected], exit.Outputs);
		Assert.Equal("fake engine output\n", File.ReadAllText(expected));
		Assert.Empty(_f.Entries(_f.Jobs));
		Assert.Equal(["clip.txt"], _f.Entries(_f.Outputs));
	}

	[Fact]
	public async Task An_existing_file_is_never_overwritten_the_output_is_numbered()
	{
		await _f.InstallAsync();
		var existing = Path.Combine(_f.Outputs, "clip.txt");
		File.WriteAllText(existing, "precious");

		var exit = await _f.RunAsync(_f.Request(["clip.txt"], Literal("--fake-write"), Output(0)));

		Assert.Equal([Path.Combine(_f.Outputs, "clip 2.txt")], exit.Outputs);
		Assert.Equal("precious", File.ReadAllText(existing));
	}

	[Fact]
	public async Task A_file_that_appears_between_the_check_and_the_move_is_not_overwritten()
	{
		await _f.InstallAsync();
		var existing = Path.Combine(_f.Outputs, "clip.txt");
		_f.Runner.BeforeMove = _ =>
		{
			if (!File.Exists(existing)) File.WriteAllText(existing, "arrived late");
		};

		var exit = await _f.RunAsync(_f.Request(["clip.txt"], Literal("--fake-write"), Output(0)));

		Assert.Equal("arrived late", File.ReadAllText(existing));
		Assert.Equal([Path.Combine(_f.Outputs, "clip 2.txt")], exit.Outputs);
	}

	[Fact]
	public async Task Several_outputs_are_all_kept_or_none_are()
	{
		await _f.InstallAsync();

		var both = await _f.RunAsync(_f.Request(["a.txt", "b.txt"], Literal("--fake-write"), Output(0), Literal("--fake-write"), Output(1)));
		Assert.Equal([Path.Combine(_f.Outputs, "a.txt"), Path.Combine(_f.Outputs, "b.txt")], both.Outputs);

		var onlyFirst = await _f.RunAsync(_f.Request(["c.txt", "d.txt"], Literal("--fake-write"), Output(0)));
		Assert.Empty(onlyFirst.Outputs);
		Assert.Contains("d.txt", onlyFirst.Error);
		Assert.Equal(["a.txt", "b.txt"], _f.Entries(_f.Outputs));
	}

	[Fact]
	public async Task A_job_that_fails_leaves_no_output_and_keeps_the_end_of_stderr()
	{
		await _f.InstallAsync();

		var exit = await _f.RunAsync(_f.Request(["clip.txt"], Literal("--fake-write"), Output(0), Literal("--err"), Literal("Invalid data found"), Literal("--exit"), Literal("1")));

		Assert.Equal(1, exit.Code);
		Assert.False(exit.Cancelled);
		Assert.Empty(exit.Outputs);
		Assert.Contains("Invalid data found", exit.StderrTail);
		Assert.Empty(_f.Entries(_f.Outputs));
		Assert.Empty(_f.Entries(_f.Jobs));
	}

	[Fact]
	public async Task Only_the_tail_of_stderr_is_kept()
	{
		await _f.InstallAsync();
		var args = Enumerable.Range(0, EngineRunner.StderrTailLines + 30).SelectMany(i => new[] { Literal("--err"), Literal($"e{i}") }).ToArray();

		var exit = await _f.RunAsync(_f.Request(args));

		Assert.Equal(EngineRunner.StderrTailLines, exit.StderrTail.Count);
		Assert.Equal($"e{EngineRunner.StderrTailLines + 29}", exit.StderrTail[^1]);
		Assert.DoesNotContain("e0", exit.StderrTail);
	}

	[Fact]
	public async Task Cancelling_a_job_that_has_written_its_output_leaves_no_output_file()
	{
		await _f.InstallAsync();
		var id = _f.Start(_f.Request(["clip.txt"], Literal("--fake-write"), Output(0), Literal("--err"), Literal("ready"), Literal("--sleep"), Literal("60000")));
		await _f.Until(() => _f.Events.Contains("err:ready"), "the engine to write its output");

		_f.Runner.Cancel(id, _f.Owner.Window);
		var exit = await _f.ExitAsync(1);

		Assert.True(exit.Cancelled);
		Assert.Empty(exit.Outputs);
		Assert.Empty(_f.Entries(_f.Outputs));
		Assert.Empty(_f.Entries(_f.Jobs));
	}

	[Fact]
	public async Task A_job_that_would_write_after_it_is_cancelled_never_does()
	{
		await _f.InstallAsync();
		var id = _f.Start(_f.Request(["clip.txt"], Literal("--err"), Literal("ready"), Literal("--write-after"), Literal("1500"), Output(0)));
		await _f.Until(() => _f.Events.Contains("err:ready"), "the engine to start");

		_f.Runner.Cancel(id, _f.Owner.Window);
		await _f.ExitAsync(1);
		await Task.Delay(2000, TestContext.Current.CancellationToken);

		Assert.Empty(_f.Entries(_f.Outputs));
		Assert.Empty(_f.Entries(_f.Jobs));
	}

	[Fact]
	public async Task Cancelling_kills_the_engines_children_too()
	{
		await _f.InstallAsync();
		var id = _f.Start(_f.Request(Literal("--spawn-child"), Literal("--sleep"), Literal("60000")));
		await _f.Until(() => _f.Events.Any(e => e.StartsWith("out:child ")), "the child to start");
		var child = int.Parse(_f.Events.First(e => e.StartsWith("out:child ")).Split(' ')[1]);

		_f.Runner.Cancel(id, _f.Owner.Window);
		await _f.ExitAsync(1);

		Assert.True(Gone(child), "the child outlived its job");
	}

	[Fact]
	public async Task A_child_left_behind_by_an_engine_that_exited_is_killed()
	{
		await _f.InstallAsync();
		_f.Start(_f.Request(Literal("--spawn-child")));
		await _f.Until(() => _f.Events.Any(e => e.StartsWith("out:child ")), "the child to start");
		var child = int.Parse(_f.Events.First(e => e.StartsWith("out:child ")).Split(' ')[1]);

		await _f.ExitAsync(1);

		Assert.True(Gone(child), "the child outlived the engine");
	}

	[Fact]
	public async Task Closing_the_owning_panel_kills_its_job_and_not_another_panels()
	{
		await _f.InstallAsync();
		var mine = _f.Start(_f.Request(Literal("--err"), Literal("ready"), Literal("--sleep"), Literal("60000")), new JobOwner("window-1", "panel-1"));
		_f.Start(_f.Request(Literal("--sleep"), Literal("60000")), new JobOwner("window-1", "panel-2"));
		await _f.Until(() => _f.Events.Contains("err:ready"), "the first engine to start");

		_f.Runner.KillPanelsExcept("window-1", new HashSet<string> { "panel-2" });
		var exit = await _f.ExitAsync(1);

		Assert.True(exit.Cancelled);
		Assert.Equal(mine, exit.JobId);
		Assert.Equal(1, _f.Runner.Running);
	}

	[Fact]
	public async Task A_minimized_panel_still_counts_as_open_so_its_job_keeps_running()
	{
		await _f.InstallAsync();
		_f.Start(_f.Request(Literal("--sleep"), Literal("60000")));

		_f.Runner.KillPanelsExcept(_f.Owner.Window, new HashSet<string> { _f.Owner.Panel });
		await Task.Delay(300, TestContext.Current.CancellationToken);

		Assert.Equal(1, _f.Runner.Running);
		Assert.Empty(_f.Exits);
	}

	[Fact]
	public async Task Closing_the_window_kills_every_job_of_that_window_only()
	{
		await _f.InstallAsync();
		_f.Start(_f.Request(Literal("--sleep"), Literal("60000")), new JobOwner("window-1", "panel-1"));
		_f.Start(_f.Request(Literal("--sleep"), Literal("60000")), new JobOwner("window-1", "panel-2"));
		_f.Start(_f.Request(Literal("--sleep"), Literal("60000")), new JobOwner("window-2", "panel-1"));

		_f.Runner.KillWindow("window-1");
		await _f.ExitAsync(2);

		Assert.Equal(1, _f.Runner.Running);
		Assert.All(_f.Exits, exit => Assert.True(exit.Cancelled));
		_f.Runner.KillWindow("window-2");
	}

	[Fact]
	public async Task A_window_cannot_cancel_another_windows_job()
	{
		await _f.InstallAsync();
		var id = _f.Start(_f.Request(Literal("--sleep"), Literal("60000")), new JobOwner("window-1", "panel-1"));

		Assert.Throws<UnknownJobException>(() => _f.Runner.Cancel(id, "window-2"));
		Assert.Throws<UnknownJobException>(() => _f.Runner.Cancel("nonsense", "window-1"));
		Assert.Equal(1, _f.Runner.Running);
	}

	[Fact]
	public async Task Cancelling_a_job_that_just_finished_is_not_an_error()
	{
		await _f.InstallAsync();
		var id = _f.Start(_f.Request(Literal("--exit"), Literal("0")));
		await _f.ExitAsync(1);

		_f.Runner.Cancel(id, _f.Owner.Window);
	}

	[Fact]
	public async Task At_most_three_jobs_run_at_once()
	{
		await _f.InstallAsync();
		for (var i = 0; i < EngineRunner.MaxRunning; i++) _f.Start(_f.Request(Literal("--sleep"), Literal("60000")));

		var error = Assert.Throws<TooManyJobsException>(() => _f.Start(_f.Request(Literal("--exit"), Literal("0"))));

		Assert.Contains(EngineRunner.MaxRunning.ToString(), error.Message);
		_f.Runner.KillWindow(_f.Owner.Window);
		await _f.Until(() => _f.Runner.Running == 0, "the jobs to end");
		await _f.RunAsync(_f.Request(Literal("--exit"), Literal("0")));
	}

	[Fact]
	public async Task The_engine_cannot_be_removed_while_a_job_uses_it_and_can_afterwards()
	{
		await _f.InstallAsync();
		var id = _f.Start(_f.Request(Literal("--sleep"), Literal("60000")));

		Assert.Throws<EngineInUseException>(() => _f.Store.Remove("ffmpeg"));

		_f.Runner.Cancel(id, _f.Owner.Window);
		await _f.ExitAsync(1);
		_f.Store.Remove("ffmpeg");
	}

	[Fact]
	public async Task A_chatty_engine_is_batched_and_the_newest_lines_are_kept()
	{
		using var quiet = new RunnerFixture(flushEvery: TimeSpan.FromHours(1));
		await quiet.InstallAsync();

		var exit = await quiet.RunAsync(quiet.Request(Literal("--chatty"), Literal("5000")));

		Assert.Equal(0, exit.Code);
		Assert.All(quiet.Batches, batch => Assert.True(batch.Lines.Count <= EngineRunner.MaxBatchLines));
		// The first line is the argument echo, then 5000 lines: all either delivered or counted as skipped.
		Assert.Equal(5001, quiet.Batches.Sum(b => b.Lines.Count + b.Skipped));
		Assert.True(quiet.Batches.Sum(b => b.Skipped) > 0);
		Assert.Equal("line 4999", quiet.Lines("out")[^1]);
	}

	[Fact]
	public async Task A_very_long_line_is_cut()
	{
		await _f.InstallAsync();

		await _f.RunAsync(_f.Request(Literal("--long-line"), Literal("100000")));

		var cut = _f.Lines("out").Single(l => l.StartsWith("xxx"));
		Assert.True(cut.Length <= EngineRunner.MaxLineChars + 1);
		Assert.EndsWith("…", cut);
	}

	[Fact]
	public async Task The_exit_comes_once_and_after_every_line_of_output()
	{
		await _f.InstallAsync();
		var id = _f.Start(_f.Request(Literal("--chatty"), Literal("300"), Literal("--err"), Literal("last words")));

		var exit = await _f.ExitAsync(1);
		await Task.Delay(300, TestContext.Current.CancellationToken);

		Assert.Equal(id, exit.JobId);
		Assert.Single(_f.Exits);
		Assert.Equal("exit", _f.Events[^1]);
		Assert.Contains("err:last words", _f.Events);
	}

	[Fact]
	public async Task A_cancel_and_a_panel_close_at_the_same_moment_end_the_job_once()
	{
		await _f.InstallAsync();
		var id = _f.Start(_f.Request(["clip.txt"], Literal("--fake-write"), Output(0), Literal("--sleep"), Literal("60000")));

		await Task.WhenAll(
			Task.Run(() => _f.Runner.Cancel(id, _f.Owner.Window), TestContext.Current.CancellationToken),
			Task.Run(() => _f.Runner.KillPanelsExcept(_f.Owner.Window, new HashSet<string>()), TestContext.Current.CancellationToken),
			Task.Run(() => _f.Runner.KillWindow(_f.Owner.Window), TestContext.Current.CancellationToken));
		await _f.ExitAsync(1);
		await Task.Delay(300, TestContext.Current.CancellationToken);

		Assert.Single(_f.Exits);
		Assert.True(_f.Exits[0].Cancelled);
		Assert.Empty(_f.Entries(_f.Outputs));
	}

	[Fact]
	public async Task A_close_that_arrives_after_the_output_was_placed_does_not_undo_a_finished_file()
	{
		await _f.InstallAsync();
		_f.Runner.BeforeMove = _ => _f.Runner.KillWindow(_f.Owner.Window);

		var exit = await _f.RunAsync(_f.Request(["clip.txt"], Literal("--fake-write"), Output(0)));

		Assert.False(exit.Cancelled, Text(exit));
		Assert.Equal([Path.Combine(_f.Outputs, "clip.txt")], exit.Outputs);
	}

	[Fact]
	public void Leftover_job_folders_are_removed_when_the_runner_starts()
	{
		Directory.CreateDirectory(Path.Combine(_f.Jobs, "left-over", "out"));
		File.WriteAllText(Path.Combine(_f.Jobs, "left-over", "out", "partial.mp4"), "x");

		_ = new EngineRunner(_f.Store, _f.Jobs, (_, _) => { });

		Assert.Empty(_f.Entries(_f.Jobs));
	}
}

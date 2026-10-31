using System.Text.Json;

namespace Zing.Core.Tests;

public sealed class DownloadsTests
{
	private static IEnumerable<JsonElement> Cases(string path, string section) => Vectors.Load(path).GetProperty(section).EnumerateArray();

	private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

	[Fact]
	public void The_policy_saves_what_cannot_be_shown_or_is_marked_as_a_file()
	{
		foreach (var c in Cases("src/shared/browser/downloadPolicy.vectors.json", "shouldDownload"))
		{
			var disposition = c.GetProperty("contentDisposition");
			var response = new DownloadResponse(
				c.GetProperty("canShow").GetBoolean(),
				disposition.ValueKind == JsonValueKind.Null ? null : disposition.GetString(),
				c.GetProperty("isMainFrame").GetBoolean());
			Assert.True(c.GetProperty("expected").GetBoolean() == DownloadPolicy.ShouldDownload(response), c.GetProperty("name").GetString());
		}
	}

	[Fact]
	public void A_download_gets_a_safe_numbered_name()
	{
		foreach (var c in Cases("src/shared/browser/downloadName.vectors.json", "fileName"))
		{
			var taken = Strings(c.GetProperty("taken")).ToHashSet();
			Assert.Equal(c.GetProperty("expected").GetString(), DownloadNames.FileName(c.GetProperty("filename").GetString()!, taken.Contains));
		}
	}

	private const string YtDlpVectors = "src/shared/download/ytdlp.vectors.json";

	[Fact]
	public void A_stale_extractor_is_told_from_an_ordinary_refusal()
	{
		foreach (var c in Cases(YtDlpVectors, "breakage"))
			Assert.Equal(c.GetProperty("expected").GetBoolean(), YtDlp.LooksLikeExtractorBreakage(c.GetProperty("stderr").GetString()!));
	}

	[Fact]
	public void A_failure_is_explained_plainly()
	{
		foreach (var c in Cases(YtDlpVectors, "explainContains"))
			Assert.Contains(c.GetProperty("contains").GetString()!, YtDlp.ExplainFailure(c.GetProperty("stderr").GetString()!, c.GetProperty("exitCode").GetInt32()));
		foreach (var c in Cases(YtDlpVectors, "explainEquals"))
			Assert.Equal(c.GetProperty("expected").GetString(), YtDlp.ExplainFailure(c.GetProperty("stderr").GetString()!, c.GetProperty("exitCode").GetInt32()));
	}

	[Fact]
	public void Arguments_match_the_original()
	{
		foreach (var c in Cases(YtDlpVectors, "args"))
		{
			var mode = c.GetProperty("mode").GetString() == "audio" ? MediaMode.Audio : MediaMode.Video;
			Assert.Equal(Strings(c.GetProperty("expected")), YtDlp.Args(mode, c.GetProperty("template").GetString()!, c.GetProperty("url").GetString()!));
		}
		foreach (var c in Cases(YtDlpVectors, "retry"))
			Assert.Equal(Strings(c.GetProperty("expected")), YtDlp.RetryArgs(Strings(c.GetProperty("args"))));
	}

	[Fact]
	public void Progress_lines_are_parsed()
	{
		foreach (var c in Cases(YtDlpVectors, "progress"))
		{
			var parsed = YtDlp.ParseProgress(c.GetProperty("line").GetString()!);
			var expected = c.GetProperty("expected");
			if (expected.ValueKind == JsonValueKind.Null)
			{
				Assert.Null(parsed);
				continue;
			}
			Assert.Equal(new DownloadProgress(
				expected.GetProperty("percent").GetDouble(),
				Optional(expected, "totalBytes"),
				Optional(expected, "speedBytesPerSecond"),
				Optional(expected, "etaSeconds")), parsed);
		}
	}

	private static double? Optional(JsonElement element, string name) => element.TryGetProperty(name, out var v) ? v.GetDouble() : null;

	private sealed class FakeStore(int engineAtMs, Func<int> clock) : IMarkStore
	{
		private ZoneMark? _mark;
		private bool _engineWritten;

		public ZoneMark? Final => _mark;

		private void Tick()
		{
			if (_engineWritten || engineAtMs < 0 || clock() < engineAtMs) return;
			_mark = new ZoneMark(3, "about:internet");
			_engineWritten = true;
		}

		public ZoneMark? Read()
		{
			Tick();
			return _mark;
		}

		public void Write(ZoneMark mark)
		{
			Tick();
			_mark = mark;
		}
	}

	[Theory]
	[InlineData(0)]
	[InlineData(2_500)]
	[InlineData(-1)]
	public async Task Our_mark_wins_over_the_engines_whenever_it_lands(int engineAtMs)
	{
		var now = 0;
		var store = new FakeStore(engineAtMs, () => now);
		var ours = new ZoneMark(3, "https://a.example/file.bin", "https://a.example/");
		await MarkOfTheWeb.HoldAsync(store, ours, 10_000, 100, ms =>
		{
			now += ms;
			return Task.CompletedTask;
		});
		Assert.Equal(ours, store.Final);
	}

	private sealed class FakeHost : IDownloadHost
	{
		public HashSet<string> Files { get; } = [];
		public string? OpenError { get; set; }
		public List<string> Opened { get; } = [];
		public List<string> Shown { get; } = [];
		public List<int> Cancelled { get; } = [];

		public bool FileExists(string path) => Files.Contains(path);

		public string? OpenFile(string path)
		{
			Opened.Add(path);
			return OpenError;
		}

		public void ShowInFolder(string path) => Shown.Add(path);

		public void Cancel(int id) => Cancelled.Add(id);
	}

	private static (DownloadList List, FakeHost Host, PageUiStore Ui, List<string> Notices) NewList()
	{
		var host = new FakeHost();
		var ui = new PageUiStore();
		var notices = new List<string>();
		return (new DownloadList(Path.Combine("C:", "dl"), host, ui, notices.Add), host, ui, notices);
	}

	[Fact]
	public void Two_simultaneous_downloads_of_one_name_do_not_collide()
	{
		var (list, _, _, _) = NewList();
		var first = list.Start("a.pdf", 10);
		var second = list.Start("a.pdf", 10);
		Assert.EndsWith("a.pdf", first.Path);
		Assert.EndsWith("a 2.pdf", second.Path);
	}

	[Fact]
	public void A_file_already_on_disk_is_not_overwritten()
	{
		var (list, host, _, _) = NewList();
		host.Files.Add(Path.Combine("C:", "dl", "a.pdf"));
		Assert.EndsWith("a 2.pdf", list.Start("a.pdf", 0).Path);
	}

	[Fact]
	public void Start_opens_the_panel_with_a_progressing_entry()
	{
		var (list, _, ui, _) = NewList();
		var started = list.Start("a.pdf", 100);
		Assert.True(ui.State.DownloadsOpen);
		Assert.Equal([new DownloadEntry(started.Id, "a.pdf", DownloadStatuses.Progressing, 0, 100)], ui.State.Downloads);
	}

	[Fact]
	public void Progress_waits_for_a_publish()
	{
		var (list, _, ui, _) = NewList();
		var id = list.Start("a.pdf", 100).Id;
		list.Progress(id, 40, 100);
		Assert.Equal(0, ui.State.Downloads[0].Received);
		list.Publish();
		Assert.Equal(40, ui.State.Downloads[0].Received);
	}

	[Theory]
	[InlineData(DownloadOutcome.Completed, "completed")]
	[InlineData(DownloadOutcome.Cancelled, "cancelled")]
	[InlineData(DownloadOutcome.Interrupted, "failed")]
	public void Done_sets_the_status_and_frees_the_name(DownloadOutcome outcome, string status)
	{
		var (list, _, ui, _) = NewList();
		var first = list.Start("a.pdf", 100);
		list.Done(first.Id, outcome, 100);
		Assert.Equal(status, ui.State.Downloads[0].Status);
		Assert.Equal(100, ui.State.Downloads[0].Received);
		Assert.Equal(first.Path, list.Start("a.pdf", 1).Path);
	}

	[Fact]
	public void Open_reports_a_failure_as_a_notice()
	{
		var (list, host, _, notices) = NewList();
		var started = list.Start("a.pdf", 1);
		list.Open(started.Id);
		Assert.Empty(notices);
		host.OpenError = "no app";
		list.Open(started.Id);
		Assert.Equal([$"Couldn't open {started.Path} (no app)"], notices);
	}

	[Fact]
	public void Show_and_open_of_an_unknown_download_raise()
	{
		var (list, _, _, _) = NewList();
		Assert.Equal(7, Assert.Throws<UnknownDownloadException>(() => list.Show(7)).Id);
		Assert.Throws<UnknownDownloadException>(() => list.Open(7));
	}

	[Fact]
	public void Show_reveals_the_saved_file()
	{
		var (list, host, _, _) = NewList();
		var started = list.Start("a.pdf", 1);
		list.Show(started.Id);
		Assert.Equal([started.Path], host.Shown);
	}

	[Fact]
	public void Cancel_reaches_only_downloads_in_progress()
	{
		var (list, host, _, _) = NewList();
		var running = list.Start("a.pdf", 1).Id;
		var done = list.Start("b.pdf", 1).Id;
		list.Done(done, DownloadOutcome.Completed, 1);
		list.Cancel(done);
		list.Cancel(99);
		list.Cancel(running);
		Assert.Equal([running], host.Cancelled);
	}

	[Fact]
	public void Clear_keeps_only_downloads_in_progress_and_closes_the_panel_when_none_remain()
	{
		var (list, _, ui, _) = NewList();
		var running = list.Start("a.pdf", 1).Id;
		var done = list.Start("b.pdf", 1).Id;
		list.Done(done, DownloadOutcome.Completed, 1);
		list.Clear();
		Assert.Equal([running], ui.State.Downloads.Select(d => d.Id));
		Assert.True(ui.State.DownloadsOpen);
		list.Done(running, DownloadOutcome.Cancelled, 0);
		list.Clear();
		Assert.Empty(ui.State.Downloads);
		Assert.False(ui.State.DownloadsOpen);
	}

	[Fact]
	public void Dismiss_hides_the_panel_but_keeps_the_list()
	{
		var (list, _, ui, _) = NewList();
		list.Start("a.pdf", 1);
		list.Dismiss();
		Assert.False(ui.State.DownloadsOpen);
		Assert.Single(ui.State.Downloads);
	}

	[Fact]
	public void A_failed_mark_is_recorded_on_the_entry()
	{
		var (list, _, ui, _) = NewList();
		var id = list.Start("a.pdf", 1).Id;
		list.MarkFailed(id, "access denied");
		Assert.Equal("Saved, but couldn't mark it as downloaded (access denied)", ui.State.Downloads[0].Error);
	}
}

public sealed class ZoneIdentifierTests
{
	[Fact]
	public void A_mark_round_trips_in_the_windows_format()
	{
		var mark = new ZoneMark(3, "https://example.com/file.bin", "https://example.com/");
		Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.com/\r\nHostUrl=https://example.com/file.bin\r\n", ZoneIdentifier.Format(mark));
		Assert.Equal(mark, ZoneIdentifier.Parse(ZoneIdentifier.Format(mark)));
	}

	[Fact]
	public void The_engines_generic_mark_parses_and_a_broken_one_does_not()
	{
		Assert.Equal(new ZoneMark(3, "about:internet"), ZoneIdentifier.Parse("[ZoneTransfer]\nZoneId=3\nHostUrl=about:internet "));
		Assert.Null(ZoneIdentifier.Parse("[ZoneTransfer]\nZoneId=three"));
	}
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Tests;

public sealed class PageUiTests
{
	private static JsonNode Json(PageUiState state) => JsonSerializer.SerializeToNode(state, ZingJson.Options)!;

	[Fact]
	public void The_empty_state_serialises_like_the_TS_one()
	{
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"findFocus":0,"downloads":[],"downloadsOpen":false}"""), Json(PageUiState.Empty)));
	}

	[Fact]
	public void A_full_state_serialises_like_the_TS_one()
	{
		var state = new PageUiState(
			new FindState("a", 1, 3),
			2,
			[new DownloadEntry(1, "a.pdf", DownloadStatuses.Failed, 5, 0, "oops")],
			true,
			new PermissionPrompt(4, "https://a.test", PermissionKinds.CameraAndMicrophone));
		var expected = """
			{"find":{"query":"a","active":1,"matches":3},"findFocus":2,
			"downloads":[{"id":1,"filename":"a.pdf","status":"failed","received":5,"total":0,"error":"oops"}],
			"downloadsOpen":true,"permission":{"id":4,"origin":"https://a.test","kind":"camera and microphone"}}
			""";
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), Json(state)));
	}

	[Fact]
	public void Page_ui_is_visible_for_a_find_bar_a_prompt_or_an_open_download_list()
	{
		Assert.False(PageUiState.Empty.HasVisiblePageUi);
		Assert.True((PageUiState.Empty with { Find = new FindState("", 0, 0) }).HasVisiblePageUi);
		Assert.True((PageUiState.Empty with { Permission = new PermissionPrompt(1, "o", "camera") }).HasVisiblePageUi);
		var entry = new DownloadEntry(1, "a", DownloadStatuses.Completed, 1, 1);
		Assert.False((PageUiState.Empty with { Downloads = [entry] }).HasVisiblePageUi);
		Assert.False((PageUiState.Empty with { DownloadsOpen = true }).HasVisiblePageUi);
		Assert.True((PageUiState.Empty with { Downloads = [entry], DownloadsOpen = true }).HasVisiblePageUi);
	}

	[Fact]
	public void Every_change_is_pushed()
	{
		var ui = new PageUiStore();
		var pushed = new List<PageUiState>();
		ui.Changed += pushed.Add;
		ui.Update(s => s with { FindFocus = 1 });
		Assert.Equal(1, Assert.Single(pushed).FindFocus);
	}

	[Fact]
	public void Find_directions_parse_and_reject_others()
	{
		Assert.Equal(FindDirection.Next, FindDirections.Parse("next"));
		Assert.Equal(FindDirection.Previous, FindDirections.Parse("previous"));
		Assert.Throws<ArgumentException>(() => FindDirections.Parse("up"));
	}

	private sealed class FakeFind : IFindHost
	{
		public object? ActivePage { get; set; }
		public HashSet<object> Dead { get; } = [];
		public List<string> Log { get; } = [];

		public bool IsAlive(object page) => !Dead.Contains(page);

		public void Search(object page, string query, FindDirection direction, FindMode mode) => Log.Add($"search {query} {direction} {mode}");

		public void ClearSelection(object page) => Log.Add("clear");

		public void FocusPanel() => Log.Add("panel");

		public void FocusPage(object page) => Log.Add("page");
	}

	private static (FindController Find, PageUiStore Ui, FakeFind Host, object Page) NewFind()
	{
		var ui = new PageUiStore();
		var host = new FakeFind { ActivePage = new object() };
		return (new FindController(ui, host), ui, host, host.ActivePage);
	}

	[Fact]
	public void Opening_shows_the_bar_and_asks_for_focus_each_time()
	{
		var (find, ui, host, _) = NewFind();
		find.Open();
		find.Open();
		Assert.Equal(new FindState("", 0, 0), ui.State.Find);
		Assert.Equal(2, ui.State.FindFocus);
		Assert.Equal(["panel", "panel"], host.Log);
	}

	[Fact]
	public void Typing_searches_from_the_start_and_results_update_the_counts()
	{
		var (find, ui, host, page) = NewFind();
		find.Open();
		find.Update("cat");
		Assert.Equal(["panel", "search cat Next Restart"], host.Log);
		find.OnResult(page, 4, 2);
		Assert.Equal(new FindState("cat", 2, 4), ui.State.Find);
		find.OnResult(page, 0, 1);
		Assert.Equal(new FindState("cat", 0, 0), ui.State.Find);
	}

	[Fact]
	public void Results_from_another_page_or_a_closed_bar_are_ignored()
	{
		var (find, ui, _, page) = NewFind();
		find.OnResult(page, 3, 1);
		Assert.Null(ui.State.Find);
		find.Open();
		find.OnResult(new object(), 3, 1);
		Assert.Equal(new FindState("", 0, 0), ui.State.Find);
	}

	[Fact]
	public void Clearing_the_text_clears_the_highlights()
	{
		var (find, ui, host, _) = NewFind();
		find.Open();
		find.Update("cat");
		find.Update("");
		Assert.Equal("clear", host.Log[^1]);
		Assert.Equal(new FindState("", 0, 0), ui.State.Find);
	}

	[Fact]
	public void Step_continues_the_search_and_without_a_query_opens_the_bar()
	{
		var (find, ui, host, _) = NewFind();
		find.Step(FindDirection.Next);
		Assert.NotNull(ui.State.Find);
		find.Update("cat");
		find.Step(FindDirection.Previous);
		Assert.Equal("search cat Previous Continue", host.Log[^1]);
	}

	[Fact]
	public void Reopening_searches_the_remembered_query_again()
	{
		var (find, _, host, _) = NewFind();
		find.Open();
		find.Update("cat");
		find.Close();
		host.Log.Clear();
		find.Open();
		Assert.Equal(["panel", "search cat Next Restart"], host.Log);
	}

	[Fact]
	public void Closing_hides_the_bar_clears_highlights_and_returns_focus_to_the_page()
	{
		var (find, ui, host, _) = NewFind();
		find.Open();
		host.Log.Clear();
		find.Close();
		Assert.Null(ui.State.Find);
		Assert.Equal(["clear", "page"], host.Log);
	}

	[Fact]
	public void Switching_tabs_under_an_open_bar_closes_it()
	{
		var (find, ui, host, _) = NewFind();
		find.Open();
		find.ActiveChanged();
		Assert.NotNull(ui.State.Find);
		host.ActivePage = new object();
		find.ActiveChanged();
		Assert.Null(ui.State.Find);
	}

	[Fact]
	public void A_dead_page_is_not_searched()
	{
		var (find, _, host, page) = NewFind();
		find.Open();
		host.Dead.Add(page);
		host.Log.Clear();
		find.Update("cat");
		find.Step(FindDirection.Next);
		Assert.Empty(host.Log);
	}
}

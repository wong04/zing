using Zing.Core;

namespace Zing.Core.Tests;

public sealed class TabOverviewTests
{
	private readonly FakePageHost _host = new();
	private readonly TabService _tabs;
	private readonly TabOverview _overview;

	public TabOverviewTests()
	{
		_tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { });
		_overview = new TabOverview(_tabs, () => _tabs.State("", false), _ => { }, (id, error) => throw new InvalidOperationException($"capture of {id} failed", error));
		_tabs.Leaving += _overview.Leaving;
	}

	[Fact]
	public void Switching_tabs_takes_no_picture_until_the_overview_has_been_opened()
	{
		var first = _tabs.Open("https://one.test/");
		var second = _tabs.Open("https://two.test/");
		_tabs.Activate(first.Id);
		Assert.Equal(0, _host.PageFor(_tabs, first.Id).Captures + _host.PageFor(_tabs, second.Id).Captures);
		Assert.Null(_overview.Thumbnail(first.Id));
	}

	[Fact]
	public async Task Once_the_overview_has_been_opened_a_tab_leaving_the_screen_is_pictured()
	{
		var first = _tabs.Open("https://one.test/");
		var second = _tabs.Open("https://two.test/");
		await _overview.OpenAsync();
		_overview.Close();
		_tabs.Activate(first.Id);
		// Once as the overview opened on it, once as it left the screen.
		Assert.Equal(2, _host.PageFor(_tabs, second.Id).Captures);
		Assert.NotNull(_overview.Thumbnail(second.Id));
	}
}

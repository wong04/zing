namespace Zing.Core.Tests.Mcp;

/// <summary>What the AI page driver needs from a window's tabs: every navigation and popup, pages on demand, and shared tabs kept awake.</summary>
public sealed class AiTabHooksTests
{
	private readonly FakePageHost _host = new();
	private readonly TabService _tabs;

	public AiTabHooksTests() => _tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { });

	[Fact]
	public void EveryCommittedNavigationIsReported()
	{
		var tab = _tabs.Open("https://a.test/");
		var seen = new List<(string, string)>();
		_tabs.PageNavigated += (id, url) => seen.Add((id, url));

		_host.PageFor(_tabs, tab.Id).Events.Navigated("https://b.test/next");

		Assert.Equal([(tab.Id, "https://b.test/next")], seen);
	}

	[Fact]
	public void APopupThatBecomesATabIsReportedWithItsOpener()
	{
		var opener = _tabs.Open("https://a.test/");
		var seen = new List<(string, string, string)>();
		_tabs.PopupOpened += (from, id, url) => seen.Add((from, id, url));

		_host.PageFor(_tabs, opener.Id).Events.Popup(new PopupRequest("https://a.test/pop", default, HasPopupFeatures: false));

		var popup = Assert.Single(seen);
		Assert.Equal(opener.Id, popup.Item1);
		Assert.NotEqual(opener.Id, popup.Item2);
		Assert.Equal("https://a.test/pop", popup.Item3);
	}

	[Fact]
	public void PageForStartsAPageWithoutShowingTheTab()
	{
		var background = _tabs.Create("https://background.test/");
		var front = _tabs.Open("https://front.test/");

		var page = (FakePage)_tabs.PageFor(background.Id);

		Assert.Same(page, _tabs.PageOf(background.Id));
		Assert.False(page.Shown);
		Assert.Equal(front.Id, _tabs.ActiveId);
		Assert.Same(page, _tabs.PageFor(background.Id));
	}

	[Fact]
	public void KeptAwakeTabsAreOutOfSightOfTheSweeps()
	{
		var shared = _tabs.Open("https://shared.test/");
		var other = _tabs.Open("https://other.test/");
		_tabs.KeptAwake = id => id == shared.Id;
		var overview = new TabOverview(_tabs, () => _tabs.State("", false), _ => { }, (_, _) => { });

		var lifecycle = new LifecycleTabs(_tabs, overview, TimeSpan.FromSeconds(5));

		Assert.Equal([other.Id], lifecycle.Records.Select(r => r.Id));
	}
}

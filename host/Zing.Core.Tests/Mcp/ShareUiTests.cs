using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

/// <summary>What the share UI is built on: the tab menu's share item, and the events the strip, badge and Settings follow.</summary>
public sealed class ShareUiTests : IDisposable
{
	private readonly FakeWindowHost _host = new();
	private readonly ChromeApi _api;
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-shareui-").FullName;

	public ShareUiTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(new Bridge(), new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), _host, "test");
		_api.Start([], "https://example.com/");
	}

	public void Dispose()
	{
		_api.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private sealed class Sharing : ITabSharing
	{
		public readonly HashSet<string> Ids = [];
		public bool IsShared(string tabId) => Ids.Contains(tabId);
		public void Share(string tabId) => Ids.Add(tabId);
		public void Unshare(string tabId) => Ids.Remove(tabId);
	}

	private IReadOnlyList<MenuItem> MenuOf(string id, ITabSharing? sharing) => Menus.Tab(_api.Tabs, _api.Saved, id, _host.Shell, _api, sharing);

	[Fact]
	public void The_tab_menu_shares_a_web_page_and_then_offers_to_stop()
	{
		var sharing = new Sharing();
		var id = _api.Tabs.ActiveId!;
		MenuOf(id, sharing).Single(m => m.Label == "Let AI use this tab").Click!();
		Assert.Contains(id, sharing.Ids);
		var menu = MenuOf(id, sharing);
		Assert.DoesNotContain(menu, m => m.Label == "Let AI use this tab");
		menu.Single(m => m.Label == "Stop AI using this tab").Click!();
		Assert.Empty(sharing.Ids);
	}

	[Fact]
	public void A_zing_page_has_no_share_item()
	{
		var zing = _api.Tabs.Open(TabStore.NewTabUrl);
		Assert.DoesNotContain(MenuOf(zing.Id, new Sharing()), m => m.Label?.Contains("AI") == true);
	}

	[Fact]
	public void Without_AI_control_the_menu_has_no_share_item() =>
		Assert.DoesNotContain(MenuOf(_api.Tabs.ActiveId!, null), m => m.Label?.Contains("AI") == true);

	[Fact]
	public void Sharing_a_tab_raises_Shared_once()
	{
		var shares = new AiShares();
		var raised = new List<TabRef>();
		shares.Shared += raised.Add;
		var tab = new TabRef("w", "t");
		shares.Share(tab, new Uri("https://example.com/"));
		shares.Share(tab, new Uri("https://example.com/"));
		Assert.Equal([tab], raised);
	}

	[Fact]
	public void Setting_a_level_raises_Changed_after_it_is_stored()
	{
		var policy = new SitePolicy(Path.Combine(_dir, "ai-sites.json"));
		var seen = new List<bool>();
		policy.Changed += () => seen.Add(policy.HasRules);
		policy.Set(new Uri("https://shop.example.com/"), SiteLevel.Never);
		policy.Set(new Uri("https://shop.example.com/"), SitePolicy.Default);
		Assert.Equal([true, false], seen);
	}

	[Theory]
	[InlineData("https://shop.example.com/", true)]
	// PublicSuffix keeps an IP address whole, so it has a site of its own.
	[InlineData("http://127.0.0.1:8080/", true)]
	[InlineData("http://localhost/", false)]
	public void Only_an_address_with_a_site_can_be_always_allowed(string url, bool hasSite) => Assert.Equal(hasSite, SitePolicy.HasSite(new Uri(url)));
}

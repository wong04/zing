using Zing.Core.Net;

namespace Zing.Core.Tests;

/// <summary>Link inspection as a tab meets it: the gate is asked on each main-frame navigation and follows the setting live.</summary>
public sealed class ThreatNavigationTests : IDisposable
{
	private const string Flagged = "https://apple.com.secure-login.tk/verify";

	private readonly SettingsStore _settings = new();
	private readonly ChromeApi _api;

	public ThreatNavigationTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var services = new AppServices(_settings, library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), Threats: ThreatGate.ForSettings(_settings));
		_api = new ChromeApi(new Bridge(), services, new FakeWindowHost(), "test");
		_api.Start([], "about:blank");
	}

	public void Dispose() => _api.Dispose();

	private IPageEvents Events => ((FakePage)_api.Tabs.ActivePage!).Events;

	[Fact]
	public void The_setting_is_on_by_default_and_a_flagged_link_goes_to_the_warning()
	{
		var decision = Assert.IsType<NavigationDecision.Redirect>(Events.Navigating("https://news.example.org/", Flagged));
		Assert.StartsWith("zing://warning?to=", decision.Url);
	}

	[Fact]
	public void Turning_the_setting_off_needs_no_restart()
	{
		_settings.Set("securityRealtime", false);
		Assert.Equal(NavigationDecision.Allow, Events.Navigating("https://news.example.org/", Flagged));
		_settings.Set("securityRealtime", true);
		Assert.IsType<NavigationDecision.Redirect>(Events.Navigating("https://news.example.org/", Flagged));
	}

	[Fact]
	public void A_clean_link_is_left_alone() =>
		Assert.Equal(NavigationDecision.Allow, Events.Navigating("https://news.example.org/", "https://github.com/"));

	[Fact]
	public void A_web_page_linking_to_visit_anyway_is_cancelled_and_the_host_is_not_allowed()
	{
		var forged = "zing://visit-anyway?to=" + Uri.EscapeDataString(Flagged) + "&k=1";
		Assert.Equal(NavigationDecision.Cancel, Events.Navigating("https://evil.example/", forged));
		Assert.IsType<NavigationDecision.Redirect>(Events.Navigating("https://news.example.org/", Flagged));
	}

	[Fact]
	public void The_warning_is_shown_on_the_tab_while_the_flagged_page_never_loads()
	{
		var decision = Assert.IsType<NavigationDecision.Redirect>(Events.Navigating("https://news.example.org/", Flagged));
		Assert.Equal(decision.Url, _api.Tabs.Records.Single().Url);
	}
}

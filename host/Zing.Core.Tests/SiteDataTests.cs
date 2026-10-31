using Zing.Core;

namespace Zing.Core.Tests;

// Mirrors src/main/site-usage.test.ts.
public sealed class SiteUsageTests
{
	private readonly SiteUsage _usage = new();

	[Fact]
	public void Every_origin_of_a_site_is_purged_when_its_last_tab_closes()
	{
		_usage.Visit("t1", "https://mail.example.com/inbox");
		_usage.Visit("t1", "https://docs.example.com/");
		var site = Assert.Single(_usage.Close("t1"));
		Assert.Equal("example.com", site.Domain);
		Assert.Equal(["https://mail.example.com", "https://docs.example.com"], site.Origins);
	}

	[Fact]
	public void A_site_another_tab_still_uses_is_kept()
	{
		_usage.Visit("t1", "https://example.com/a");
		_usage.Visit("t2", "https://www.example.com/b");
		Assert.Empty(_usage.Close("t1"));
		Assert.Equal("example.com", Assert.Single(_usage.Close("t2")).Domain);
	}

	[Fact]
	public void A_tab_that_moved_on_still_used_the_earlier_site()
	{
		_usage.Visit("t1", "https://a.test/");
		_usage.Visit("t1", "https://b.test/");
		Assert.Equal(["a.test", "b.test"], _usage.Close("t1").Select(s => s.Domain));
	}

	[Fact]
	public void Non_web_pages_and_unknown_tabs_are_ignored()
	{
		_usage.Visit("t1", "about:blank");
		_usage.Visit("t1", "not a url");
		_usage.Visit("t1", "zing://home");
		Assert.Empty(_usage.Close("t1"));
		Assert.Empty(_usage.Close("nope"));
	}

	[Fact]
	public void A_site_is_not_purged_twice()
	{
		_usage.Visit("t1", "http://127.0.0.1:8080/");
		Assert.Single(_usage.Close("t1"));
		Assert.Empty(_usage.Close("t1"));
	}
}

public sealed class SiteDataKeeperTests
{
	private sealed class Purger : ISitePurger
	{
		public List<string> Purged { get; } = [];
		public bool Fail { get; set; }

		public Task PurgeAsync(SiteToPurge site)
		{
			if (Fail) throw new IOException("engine gone");
			Purged.Add(site.Domain);
			return Task.CompletedTask;
		}
	}

	[Fact]
	public void Closing_the_last_tab_of_a_site_purges_it_and_a_failure_is_reported_not_thrown()
	{
		var purger = new Purger();
		var failures = new List<string>();
		var keeper = new SiteDataKeeper(purger, (domain, _) => failures.Add(domain));
		keeper.Visit("t1", "https://example.com/");
		keeper.Release("t1");
		Assert.Equal(["example.com"], purger.Purged);

		purger.Fail = true;
		keeper.Visit("t2", "https://other.test/");
		keeper.Release("t2");
		Assert.Equal(["other.test"], failures);
	}

	[Fact]
	public void Tabs_closed_by_the_service_release_their_sites()
	{
		var purger = new Purger();
		var keeper = new SiteDataKeeper(purger, (_, _) => { });
		var tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), new FakePageHost(), "about:blank", () => { }, (_, _) => { }, siteData: keeper);
		var tab = tabs.Open("about:blank");
		((FakePage)tabs.PageOf(tab.Id)!).Events.Navigated("https://example.com/");
		tabs.Close(tab.Id);
		Assert.Equal(["example.com"], purger.Purged);
	}
}

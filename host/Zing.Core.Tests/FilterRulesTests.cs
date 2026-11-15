namespace Zing.Core.Tests;

public sealed class FilterRulesTests
{
	private static readonly FilterRules Rules = FilterRules.Parse(
	[
		"[Adblock Plus 2.0]",
		"! a comment",
		"||ads.example^",
		"||track.example^$third-party",
		"||popups.example^$popup",
		"||scripts.example^$script",
		"||paths.example^*/banner.js",
		"||fine.example^$domain=news.example",
		"||ALLOWED.example^",
		"@@||ok.allowed.example^$image,domain=shop.example",
		"||hidden.example^",
		"@@||hidden.example^$generichide",
		"example.com##.ad-box",
		"||cdn.example^",
	]);

	[Theory]
	// Plain rules refuse the host and everything under it, from any page.
	[InlineData("ads.example", "https://news.example/", true)]
	[InlineData("img.ads.example", "https://news.example/", true)]
	[InlineData("ads.example", "https://ads.example/", true)]
	[InlineData("IMG.Ads.Example.", "https://news.example/", true)]
	// Only the host and its subdomains: a longer name or the name inside another is not it.
	[InlineData("badads.example", "https://news.example/", false)]
	[InlineData("ads.example.com", "https://news.example/", false)]
	// $third-party: refused from another site, loaded on its own.
	[InlineData("track.example", "https://news.example/", true)]
	[InlineData("cdn.track.example", "https://www.track.example/", false)]
	[InlineData("track.example", "zing://home", true)]
	// Rules with other options, or a path, are not read.
	[InlineData("popups.example", "https://news.example/", false)]
	[InlineData("scripts.example", "https://news.example/", false)]
	[InlineData("paths.example", "https://news.example/", false)]
	[InlineData("fine.example", "https://news.example/", false)]
	// Any exception unblocks the host it names and what is under it, but not the domain above.
	[InlineData("ok.allowed.example", "https://news.example/", false)]
	[InlineData("img.ok.allowed.example", "https://news.example/", false)]
	[InlineData("allowed.example", "https://news.example/", true)]
	// An exception that only turns off element hiding leaves blocking on.
	[InlineData("hidden.example", "https://news.example/", true)]
	[InlineData("unlisted.example", "https://news.example/", false)]
	[InlineData("", "https://news.example/", false)]
	public void Domain_rules_decide_by_host_and_party(string host, string page, bool blocked) =>
		Assert.Equal(blocked, Rules.Blocks(host, page));

	[Fact]
	public void Comments_cosmetic_and_unread_rules_add_no_hosts() =>
		// ads, track, allowed, ok.allowed (exception), hidden, cdn.
		Assert.Equal(6, Rules.Count);

	[Fact]
	public void Lists_without_domain_rules_are_refused() =>
		Assert.Throws<InvalidDataException>(() => FilterRules.Parse(["! only a comment", "example.com##.ad"]));

	[Fact]
	public void The_bundled_lists_load_and_block_known_ad_and_tracker_servers()
	{
		Assert.True(FilterRules.Bundled.Count > 50_000);
		Assert.True(FilterRules.Bundled.Blocks("ads.adform.net", "https://news.example/"));
		Assert.True(FilterRules.Bundled.Blocks("c.statcounter.com", "https://news.example/"));
		Assert.False(FilterRules.Bundled.Blocks("www.wikipedia.org", "https://news.example/"));
	}

	[Fact]
	public void Checking_a_host_the_lists_do_not_name_allocates_nothing()
	{
		// Every request a page makes is checked on the UI thread.
		Rules.Blocks("warm.up.example.com", "https://news.example/");
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 1000; i++)
		{
			Rules.Blocks("cdn.images.example.com", "https://news.example/");
			Rules.Blocks("img.ads.example", "https://news.example/");
		}
		Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
	}
}

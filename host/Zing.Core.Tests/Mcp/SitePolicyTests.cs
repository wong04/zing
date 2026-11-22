using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class SitePolicyTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-sitepolicy-").FullName;
	private static readonly AxNode Page = new("document", "", false, false, null);
	private static readonly AxNode PayButton = new("button", "Pay now", false, true, null);

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private string PolicyPath => Path.Combine(_dir, "ai-sites.json");

	[Fact]
	public void DefaultIsAskSubmit()
	{
		var policy = new SitePolicy(PolicyPath);
		var url = new Uri("https://shop.example.com/cart");
		Assert.Equal(SiteLevel.AskSubmit, policy.LevelFor(url));
		Assert.True(policy.NeedsConfirm(url, AiAction.Click, PayButton, null));
		Assert.False(policy.NeedsConfirm(url, AiAction.Scroll, Page, null));
		Assert.False(policy.HasRules);
		Assert.False(File.Exists(PolicyPath));
	}

	[Fact]
	public void NeverSkipsAllConfirms()
	{
		var policy = new SitePolicy(PolicyPath);
		var url = new Uri("https://shop.example.com/cart");
		policy.Set(url, SiteLevel.Never);
		Assert.False(policy.NeedsConfirm(url, AiAction.Click, PayButton, null));
	}

	[Fact]
	public void AskAllConfirmsScroll()
	{
		var policy = new SitePolicy(PolicyPath);
		var url = new Uri("https://shop.example.com/cart");
		policy.Set(url, SiteLevel.AskAll);
		Assert.True(policy.NeedsConfirm(url, AiAction.Scroll, Page, null));
	}

	[Fact]
	public void GithubIoUsersAreSeparateSites()
	{
		var policy = new SitePolicy(PolicyPath);
		policy.Set(new Uri("https://alice.github.io/"), SiteLevel.Never);
		Assert.Equal(SiteLevel.Never, policy.LevelFor(new Uri("https://alice.github.io/page")));
		Assert.Equal(SiteLevel.AskSubmit, policy.LevelFor(new Uri("https://bob.github.io/")));
	}

	[Fact]
	public void SameSiteSubdomainsShareALevel()
	{
		var policy = new SitePolicy(PolicyPath);
		policy.Set(new Uri("https://www.example.com/"), SiteLevel.AskAll);
		Assert.Equal(SiteLevel.AskAll, policy.LevelFor(new Uri("https://mail.example.com/")));
	}

	[Fact]
	public void LevelsSurviveReload()
	{
		new SitePolicy(PolicyPath).Set(new Uri("https://example.com/"), SiteLevel.Never);
		var reloaded = new SitePolicy(PolicyPath);
		Assert.Equal(SiteLevel.Never, reloaded.LevelFor(new Uri("https://example.com/")));
		Assert.Equal(SiteLevel.Never, reloaded.All()["example.com"]);
		Assert.True(reloaded.HasRules);
	}

	[Fact]
	public void SettingBackToDefaultRemovesTheRuleAndTheFile()
	{
		var policy = new SitePolicy(PolicyPath);
		var url = new Uri("https://example.com/");
		policy.Set(url, SiteLevel.Never);
		policy.Set(url, SiteLevel.AskSubmit);
		Assert.Empty(policy.All());
		Assert.False(policy.HasRules);
		Assert.False(File.Exists(PolicyPath));
	}

	[Fact]
	public void UnreadableFileIsAnError()
	{
		File.WriteAllText(PolicyPath, "not json");
		Assert.Throws<SitePolicyFileException>(() => new SitePolicy(PolicyPath));
	}

	[Fact]
	public void UnknownLevelIsAnError()
	{
		File.WriteAllText(PolicyPath, """{"example.com":"Sometimes"}""");
		Assert.Throws<SitePolicyFileException>(() => new SitePolicy(PolicyPath));
	}

	[Fact]
	public void PageWithNoSiteCannotBeSet()
	{
		var policy = new SitePolicy(PolicyPath);
		Assert.Throws<NoSiteException>(() => policy.Set(new Uri("https://com/"), SiteLevel.Never));
	}
}

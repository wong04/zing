using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class FormPostBackstopTests : IDisposable
{
	private static readonly TabRef Tab = new("w1", "t1");
	private static readonly Uri Shop = new("https://shop.example.com/cart");

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-backstop-").FullName;
	private readonly SitePolicy _policy;
	private readonly FormPostBackstop _backstop;
	private DateTime _now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

	public FormPostBackstopTests()
	{
		_policy = new SitePolicy(Path.Combine(_dir, "ai-sites.json"));
		_backstop = new FormPostBackstop(_policy, () => _now);
	}

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	[Fact]
	public void APostRightAfterAnUnconfirmedActionIsHeld()
	{
		_backstop.Acted(Tab, confirmed: false);
		_now += TimeSpan.FromSeconds(1);
		Assert.True(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));
	}

	[Fact]
	public void OnlyOnePostIsHeldPerAction()
	{
		_backstop.Acted(Tab, confirmed: false);
		Assert.True(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));
		Assert.False(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));
	}

	[Fact]
	public void APostAfterTheWindowGoes()
	{
		_backstop.Acted(Tab, confirmed: false);
		_now += FormPostBackstop.Window + TimeSpan.FromMilliseconds(1);
		Assert.False(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));
	}

	[Fact]
	public void APostWithNoAiActionGoes() => Assert.False(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));

	[Theory]
	[InlineData("GET", true)]
	[InlineData("post", false)]
	public void GetsAndFramePostsGo(string method, bool mainFrame)
	{
		_backstop.Acted(Tab, confirmed: false);
		Assert.False(_backstop.Holds(Tab, Shop, method, mainFrame));
	}

	[Fact]
	public void AMethodIsMatchedWhateverItsCase()
	{
		_backstop.Acted(Tab, confirmed: false);
		Assert.True(_backstop.Holds(Tab, Shop, "post", mainFrame: true));
	}

	[Fact]
	public void AnActionTheOwnerAllowedLetsItsPostGo()
	{
		_backstop.Acted(Tab, confirmed: true);
		Assert.False(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));
	}

	[Fact]
	public void ADontAskSiteIsNeverHeld()
	{
		_policy.Set(Shop, SiteLevel.Never);
		_backstop.Acted(Tab, confirmed: false);
		Assert.False(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));
	}

	[Fact]
	public void AnAskAllSiteIsHeld()
	{
		_policy.Set(Shop, SiteLevel.AskAll);
		_backstop.Acted(Tab, confirmed: false);
		Assert.True(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));
	}

	[Fact]
	public void AnotherTabsActionDoesNotCount()
	{
		_backstop.Acted(new TabRef("w1", "t2"), confirmed: false);
		Assert.False(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));
	}

	[Fact]
	public void ForgottenTabsAreNotHeld()
	{
		_backstop.Acted(Tab, confirmed: false);
		_backstop.Forget(Tab);
		Assert.False(_backstop.Holds(Tab, Shop, "POST", mainFrame: true));
	}
}

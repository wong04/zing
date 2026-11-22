using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class AiSharesTests
{
	private static readonly TabRef Tab = new("w1", "t1");
	private static readonly Uri Web = new("https://example.com/");

	[Theory]
	[InlineData("zing://home")]
	[InlineData("file:///C:/")]
	[InlineData("javascript:x")]
	public void SharingZingOrFilePageThrows(string url)
	{
		var shares = new AiShares();
		var error = Assert.Throws<NotShareableException>(() => shares.Share(Tab, new Uri(url)));
		Assert.DoesNotContain(url, error.Message);
		Assert.False(shares.IsShared(Tab));
	}

	[Fact]
	public void NavigatingToZingUnsharesBeforeReturning()
	{
		var shares = new AiShares();
		var aiId = shares.Share(Tab, Web);
		var raised = new List<TabRef>();
		shares.Unshared += raised.Add;

		shares.Navigated(Tab, new Uri("zing://settings"));

		Assert.Equal([Tab], raised);
		Assert.False(shares.IsShared(Tab));
		Assert.False(shares.TryResolve(aiId, out _));
	}

	[Fact]
	public void NavigatingToAllowedUrlKeepsShare()
	{
		var shares = new AiShares();
		var aiId = shares.Share(Tab, Web);
		shares.Navigated(Tab, new Uri("https://other.example/"));
		Assert.True(shares.TryResolve(aiId, out var tab));
		Assert.Equal(Tab, tab);
	}

	[Fact]
	public void PopupFromSharedTabIsShared()
	{
		var shares = new AiShares();
		shares.Share(Tab, Web);
		var popup = new TabRef("w1", "t2");
		shares.PopupFrom(Tab, popup, Web);
		Assert.True(shares.IsShared(popup));
	}

	[Fact]
	public void PopupFromUnsharedTabOrToDisallowedUrlIsNotShared()
	{
		var shares = new AiShares();
		var popup = new TabRef("w1", "t2");
		shares.PopupFrom(Tab, popup, Web);
		Assert.False(shares.IsShared(popup));

		shares.Share(Tab, Web);
		shares.PopupFrom(Tab, popup, new Uri("zing://home"));
		Assert.False(shares.IsShared(popup));
	}

	[Fact]
	public void AiIdIsNotTheTabId()
	{
		var shares = new AiShares();
		var aiId = shares.Share(Tab, Web);
		Assert.NotEqual(Tab.TabId, aiId);
		Assert.NotEqual(aiId, shares.Share(new TabRef("w1", "t2"), Web));
	}

	[Fact]
	public void SharingTwiceReturnsSameAiId()
	{
		var shares = new AiShares();
		Assert.Equal(shares.Share(Tab, Web), shares.Share(Tab, Web));
		Assert.Single(shares.List());
	}

	[Fact]
	public void UnshareAllRaisesUnsharedForEach()
	{
		var shares = new AiShares();
		var other = new TabRef("w2", "t9");
		shares.Share(Tab, Web);
		shares.Share(other, Web);
		var raised = new List<TabRef>();
		shares.Unshared += raised.Add;

		shares.UnshareAll();

		Assert.Equal(2, raised.Count);
		Assert.Contains(Tab, raised);
		Assert.Contains(other, raised);
		Assert.Empty(shares.List());
	}

	[Fact]
	public void UnshareOfUnsharedTabRaisesNothing()
	{
		var shares = new AiShares();
		shares.Unshared += _ => throw new InvalidOperationException("unexpected");
		shares.Unshare(Tab);
	}
}

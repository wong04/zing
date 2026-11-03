using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class RelayedNavigationTests
{
	private const string Relay = "zing-relay://a.com/x";
	private const string Web = "http://127.0.0.1:5000/";

	private static NavigationDecision Decide(string from, string to, NavigationOrigin origin = NavigationOrigin.Page, bool redirected = false, bool chainIsRelay = false) =>
		RelayedNavigation.MainFrame(new MainFrameNavigation(from, to, origin, redirected, chainIsRelay));

	[Fact]
	public void AWebPageCannotNavigateToTheRelay() => Assert.Equal(NavigationDecision.Cancel, Decide(Web, Relay));

	[Fact]
	public void ARedirectFromAWebPageIsCancelled() => Assert.Equal(NavigationDecision.Cancel, Decide(Web, Relay, redirected: true));

	[Fact]
	public void ARedirectInsideARelayChainIsAllowed() => Assert.Equal(NavigationDecision.Allow, Decide(Web, Relay, redirected: true, chainIsRelay: true));

	[Fact]
	public void NavigationZingStartedIsAllowed() => Assert.Equal(NavigationDecision.Allow, Decide(Web, Relay, NavigationOrigin.Zing));

	[Theory]
	[InlineData(NavigationOrigin.Reload)]
	[InlineData(NavigationOrigin.History)]
	public void ReloadAndHistoryAreAllowed(NavigationOrigin origin) => Assert.Equal(NavigationDecision.Allow, Decide(Web, Relay, origin));

	[Fact]
	public void ARelayedPageMayGoToAnotherRelayedPage() => Assert.Equal(NavigationDecision.Allow, Decide(Relay, "zing-relay://b.com/"));

	[Fact]
	public void ARelayedPageLinkingToHttpsStaysInTheRelay() =>
		Assert.Equal(new NavigationDecision.Redirect("zing-relay://b.com/p"), Decide(Relay, "https://b.com/p"));

	[Theory]
	[InlineData("zing://home")]
	[InlineData("file:///c:/secret.txt")]
	[InlineData("https://app.zing/index.html#chrome")]
	[InlineData("http://b.com/")]
	[InlineData("javascript:alert(1)")]
	[InlineData("data:text/html,x")]
	public void ARelayedPageCannotLeaveTheRelay(string to)
	{
		var decision = Decide(Relay, to);
		Assert.True(decision == NavigationDecision.Cancel || decision is NavigationDecision.Redirect { Url: var u } && RelayedContent.IsRelayUrl(u));
		Assert.NotEqual(NavigationDecision.Allow, decision);
	}

	[Fact]
	public void TheAddressBarMayLeaveTheRelay() => Assert.Equal(NavigationDecision.Allow, Decide(Relay, "zing://home", NavigationOrigin.Zing));

	[Fact]
	public void OrdinaryNavigationIsUntouched() => Assert.Equal(NavigationDecision.Allow, Decide(Web, "https://example.com/"));

	[Fact]
	public void FramesGoToTheRelayOnlyFromARelayedPage()
	{
		Assert.False(RelayedNavigation.FrameAllowed(Web, Relay));
		Assert.True(RelayedNavigation.FrameAllowed(Relay, "zing-relay://b.com/"));
		Assert.True(RelayedNavigation.FrameAllowed(Web, "https://x.com/"));
		Assert.False(RelayedNavigation.FrameAllowed(Relay, "https://x.com/"));
		Assert.False(RelayedNavigation.FrameAllowed(Relay, "file:///c:/x"));
		Assert.True(RelayedNavigation.FrameAllowed(Relay, "about:blank"));
	}

	[Fact]
	public void NewWindowsFollowTheSameRule()
	{
		Assert.False(RelayedNavigation.NewWindowAllowed(Web, Relay));
		Assert.True(RelayedNavigation.NewWindowAllowed(Relay, "zing-relay://b.com/"));
		Assert.False(RelayedNavigation.NewWindowAllowed(Relay, "zing://home"));
		Assert.True(RelayedNavigation.NewWindowAllowed(Web, "https://x.com/"));
	}

	[Fact]
	public void ARelayedPageCausesNoDocumentRequestToTheNetworkExceptForANavigationZingAllowed()
	{
		const string Typed = "https://news.example/";
		Assert.False(RelayedNavigation.MayRequestDocument(Relay, "http://127.0.0.1:5000/nav", approvedDocument: null));
		Assert.False(RelayedNavigation.MayRequestDocument(Relay, "https://other.test/x", approvedDocument: Relay));
		Assert.True(RelayedNavigation.MayRequestDocument(Relay, Typed, approvedDocument: Typed));
		Assert.True(RelayedNavigation.MayRequestDocument(Web, "https://news.example/", approvedDocument: null));
	}

	[Fact]
	public void AFragmentIsNotPartOfTheRequestSoItDoesNotBreakTheMatch()
	{
		// The engine asks for the address without its fragment, while the navigation event carries it.
		Assert.True(RelayedNavigation.MayRequestDocument(Relay, "https://news.example/page", approvedDocument: "https://news.example/page#sec"));
		Assert.True(RelayedNavigation.MayServe(Web, "zing-relay://a.com/x", isDocument: true, approvedDocument: "zing-relay://a.com/x#frag"));
		Assert.False(RelayedNavigation.MayRequestDocument(Relay, "https://news.example/other", approvedDocument: "https://news.example/page#sec"));
	}

	[Fact]
	public void RelayRequestsAreServedOnlyForARelayedPageOrAnApprovedDocument()
	{
		Assert.True(RelayedNavigation.MayServe(Relay, "zing-relay://b.com/s.css", isDocument: false, approvedDocument: null));
		Assert.False(RelayedNavigation.MayServe(Web, "zing-relay://b.com/s.css", isDocument: false, approvedDocument: null));
		Assert.False(RelayedNavigation.MayServe(Web, Relay, isDocument: true, approvedDocument: null));
		Assert.True(RelayedNavigation.MayServe(Web, Relay, isDocument: true, approvedDocument: Relay));
		Assert.False(RelayedNavigation.MayServe(Web, "zing-relay://other/", isDocument: true, approvedDocument: Relay));
	}
}

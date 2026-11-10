using Zing.Core.Net;
using Zing.Core.Sync;

namespace Zing.Core.Tests.Sync;

/// <summary>What a page in the Google sign-in window may open as a tab: only what the person clicked, and only what tabs would open.</summary>
public sealed class SignInWindowLinksTests
{
	private const string Google = "https://accounts.google.com/o/oauth2/v2/auth?client_id=x";

	[Theory]
	[InlineData("https://support.google.com/accounts")]
	[InlineData("http://example.test/privacy")]
	public void ALinkThePersonClickedToAWebAddressOpensAsATab(string target) =>
		Assert.Equal(target, SignInWindowLinks.TabFor(Google, target, userInitiated: true));

	[Fact]
	public void ScriptOpeningAWindowWithoutAClickOpensNothing() =>
		Assert.Null(SignInWindowLinks.TabFor(Google, "https://support.google.com/accounts", userInitiated: false));

	[Theory]
	[InlineData(RelayedContent.Scheme + "://example.test/")]
	[InlineData(StartPage.Scheme + "://" + ThreatGate.VisitAnywayHost + "/?url=https%3A%2F%2Fbad.test")]
	[InlineData(StartPage.Scheme + "://settings")]
	[InlineData("javascript:alert(1)")]
	[InlineData("file:///C:/Windows/win.ini")]
	[InlineData("data:text/html,hi")]
	[InlineData("not an address")]
	public void ZingsOwnSchemesTheRelayAndOtherSchemesOpenNothingEvenOnAClick(string target) =>
		Assert.Null(SignInWindowLinks.TabFor(Google, target, userInitiated: true));
}

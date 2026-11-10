using Zing.Core.Import;

namespace Zing.Core.Tests.Import;

public sealed class DefaultBrowserSourceTests
{
	private static ImportSource Row(ImportBrowser browser, string? profile, bool lastUsed = false, string? error = null) =>
		new($"{browser}:{profile}", browser, profile, $"{browser} ({profile})", "unused", error is null ? 1 : null, error, lastUsed);

	[Theory]
	[InlineData("ChromeHTML", ImportBrowser.Chrome)]
	[InlineData("MSEdgeHTM", ImportBrowser.Edge)]
	[InlineData("BraveHTML", ImportBrowser.Brave)]
	[InlineData("chromehtml", ImportBrowser.Chrome)]
	[InlineData("MSEDGEHTM", ImportBrowser.Edge)]
	[InlineData("ChromeHTML.QXJ3PUHXZD5YJ2RXZSY5HSWB4Q", ImportBrowser.Chrome)]
	[InlineData("MSEdgeHTM.Beta", ImportBrowser.Edge)]
	[InlineData("bravehtml.nightly", ImportBrowser.Brave)]
	[InlineData("Arc", ImportBrowser.Arc)]
	[InlineData("ArcHTML.v1", ImportBrowser.Arc)]
	[InlineData("arcurl", ImportBrowser.Arc)]
	public void MapsKnownProgIds(string progId, ImportBrowser expected) => Assert.Equal(expected, DefaultBrowserSource.BrowserFor(progId));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("ZingHTML")]
	[InlineData("ChromeHTMLX")]
	[InlineData("ChromeHTML.")]
	[InlineData("FirefoxURL-308046B0AF4A39CB")]
	[InlineData("AppXq0fevzme2pys62n3e0fbqa7peapykr8v")]
	public void ZingOrUnknownProgIdHasNoSource(string? progId)
	{
		Assert.Null(DefaultBrowserSource.BrowserFor(progId));
		Assert.Null(DefaultBrowserSource.Pick(progId, [Row(ImportBrowser.Chrome, "Default", lastUsed: true)]));
	}

	[Fact]
	public void PicksTheLastUsedProfile()
	{
		var picked = DefaultBrowserSource.Pick("ChromeHTML", [Row(ImportBrowser.Chrome, "Default"), Row(ImportBrowser.Chrome, "Profile 1", lastUsed: true), Row(ImportBrowser.Edge, "Default", lastUsed: true)]);
		Assert.Equal("Profile 1", picked?.Profile);
	}

	[Fact]
	public void SkipsAProfileWithAnError()
	{
		var picked = DefaultBrowserSource.Pick("ChromeHTML", [Row(ImportBrowser.Chrome, "Default", lastUsed: true, error: "damaged"), Row(ImportBrowser.Chrome, "Profile 1"), Row(ImportBrowser.Chrome, "Profile 2")]);
		Assert.Equal("Profile 1", picked?.Profile);
	}

	[Fact]
	public void ABrowserWhoseOnlyRowFailedHasNoSource() =>
		Assert.Null(DefaultBrowserSource.Pick("ChromeHTML", [Row(ImportBrowser.Chrome, null, error: "damaged")]));

	[Fact]
	public void DefaultBrowserNotInstalledHasNoSource() =>
		Assert.Null(DefaultBrowserSource.Pick("BraveHTML", [Row(ImportBrowser.Chrome, "Default", lastUsed: true)]));
}

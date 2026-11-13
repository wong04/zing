using Zing.Core.Import;

namespace Zing.Core.Tests.Import;

public sealed class ExportPagesTests
{
	[Theory]
	[InlineData(ImportBrowser.Chrome, "chrome://password-manager/settings")]
	[InlineData(ImportBrowser.Edge, "edge://wallet/passwords")]
	[InlineData(ImportBrowser.Brave, "brave://password-manager/settings")]
	public void EachBrowserHasItsExportPage(ImportBrowser browser, string url) => Assert.Equal(url, ExportPages.Url(browser));

	[Fact]
	public void ArcHasNoExportPage() => Assert.Throws<NoPasswordExportException>(() => ExportPages.Url(ImportBrowser.Arc));

	[Fact]
	public void DiaHasNoExportPageZingCanOpen() => Assert.Equal("Dia has no password export page Zing can open", Assert.Throws<NoPasswordExportException>(() => ExportPages.Url(ImportBrowser.Dia)).Message);

	[Theory]
	[InlineData(ImportBrowser.Chrome, true)]
	[InlineData(ImportBrowser.Edge, true)]
	[InlineData(ImportBrowser.Brave, true)]
	[InlineData(ImportBrowser.Dia, false)]
	[InlineData(ImportBrowser.Arc, false)]
	public void OnlyBrowsersWithAnExportPageOfferPasswords(ImportBrowser browser, bool offered) => Assert.Equal(offered, ExportPages.Has(browser));

	[Fact]
	public void TheLaunchNamesTheProfileBeforeThePage() =>
		Assert.Equal(["--profile-directory=Default", "chrome://password-manager/settings"], BrowserLaunch.Arguments("Default", "chrome://password-manager/settings"));

	[Theory]
	[InlineData("Profile 1")]
	[InlineData("Profile 1\" --incognito")]
	public void AProfileWithSpacesOrQuotesStaysOneArgument(string profile)
	{
		var info = BrowserLaunch.StartInfo(@"C:\Chrome\chrome.exe", profile, "chrome://password-manager/settings");

		Assert.Equal(["--profile-directory=" + profile, "chrome://password-manager/settings"], info.ArgumentList);
		// Arguments joined into one string would let a quote in the name end the argument; Windows quotes each ArgumentList entry instead.
		Assert.Empty(info.Arguments);
		Assert.False(info.UseShellExecute);
	}
}

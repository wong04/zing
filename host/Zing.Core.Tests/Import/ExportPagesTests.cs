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
}

using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class ForcedDownloadTests
{
	[Theory]
	[InlineData(null, "attachment")]
	[InlineData("", "attachment")]
	[InlineData("inline", "attachment")]
	[InlineData("inline; filename=\"report.pdf\"", "attachment; filename=\"report.pdf\"")]
	[InlineData("attachment; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf", "attachment; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf")]
	public void TheResponseIsMarkedAsAFileKeepingTheServersName(string? original, string marked) =>
		Assert.Equal(marked, ForcedDownload.Disposition(original));
}

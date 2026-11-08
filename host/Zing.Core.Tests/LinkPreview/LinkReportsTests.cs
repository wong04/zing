using Zing.Core.LinkPreview;

namespace Zing.Core.Tests.LinkPreview;

public class LinkReportsTests
{
	[Fact]
	public void AMiddlePressIsRead() => Assert.Equal(LinkReportKind.Middle, LinkReports.Read("{\"kind\":\"middle\"}"));

	[Theory]
	[InlineData("{\"kind\":\"Middle\"}")]
	[InlineData("{\"kind\":\"other\"}")]
	[InlineData("{\"kind\":1}")]
	[InlineData("{\"kind\":\"middle\",\"x\":1}")]
	[InlineData("{\"kind\":{\"a\":\"middle\"}}")]
	[InlineData("{\"type\":\"middle\"}")]
	[InlineData("[\"middle\"]")]
	[InlineData("\"middle\"")]
	[InlineData("not json")]
	[InlineData("")]
	public void AnythingElseIsNot(string payload) => Assert.Null(LinkReports.Read(payload));

	[Fact]
	public void AnOversizedPayloadIsNotRead() => Assert.Null(LinkReports.Read("{\"kind\":\"middle\"" + new string(' ', 64) + "}"));

	[Theory]
	[InlineData(5000L, 5000L, true)]
	[InlineData(5000L, 6000L, true)]
	[InlineData(5000L, 6001L, false)]
	[InlineData(5000L, 4999L, false)]
	public void APressMakesTheRequestsWithinASecond(long pressed, long now, bool made) => Assert.Equal(made, LinkReports.MiddleMade(pressed, now));

	[Fact]
	public void NoPressMakesNone() => Assert.False(LinkReports.MiddleMade(null, 5000));

	[Fact]
	public void TheScriptCallsTheBindingWithTheKindItIsReadBy()
	{
		Assert.Contains($"globalThis.{LinkReports.BindingName}(", LinkReports.Script);
		Assert.Contains("report('middle')", LinkReports.Script);
	}
}

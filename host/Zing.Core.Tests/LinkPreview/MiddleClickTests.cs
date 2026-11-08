using Zing.Core.LinkPreview;

namespace Zing.Core.Tests.LinkPreview;

public class MiddleClickTests
{
	[Fact]
	public void TheScriptsMessageIsRead() => Assert.True(MiddleClick.IsMessage("{\"zingMiddleClick\":true}"));

	[Theory]
	[InlineData("{\"zingMiddleClick\":false}")]
	[InlineData("{\"zingMiddleClick\":1}")]
	[InlineData("{\"zingMiddleClick\":\"true\"}")]
	[InlineData("{\"zingMiddleClick\":true,\"x\":1}")]
	[InlineData("{\"zingMiddleClick\":{\"a\":true}}")]
	[InlineData("{\"zingCredentialField\":{\"kind\":\"none\"}}")]
	[InlineData("[true]")]
	[InlineData("true")]
	[InlineData("not json")]
	[InlineData("")]
	public void AnythingElseIsNot(string json) => Assert.False(MiddleClick.IsMessage(json));

	[Fact]
	public void AnOversizedMessageIsNotRead() => Assert.False(MiddleClick.IsMessage("{\"zingMiddleClick\":true" + new string(' ', 64) + "}"));

	[Theory]
	[InlineData(5000L, 5000L, true)]
	[InlineData(5000L, 6000L, true)]
	[InlineData(5000L, 6001L, false)]
	[InlineData(5000L, 4999L, false)]
	public void APressMakesTheRequestsWithinASecond(long pressed, long now, bool made) => Assert.Equal(made, MiddleClick.Made(pressed, now));

	[Fact]
	public void NoPressMakesNone() => Assert.False(MiddleClick.Made(null, 5000));

	[Fact]
	public void TheScriptPostsTheKeyItIsReadBy() => Assert.Contains($"{MiddleClick.MessageKey}: true", MiddleClick.Script);
}

namespace Zing.Core.Tests;

/// <summary>What a renderer view (Zing's own UI) may load, in its top frame or any frame: Zing's app and the viewer's files, nothing else.</summary>
public sealed class ZingOriginTests
{
	[Theory]
	[InlineData("https://app.zing/index.html#chrome")]
	[InlineData("https://app.zing/")]
	[InlineData("https://APP.ZING/index.html#surface")]
	[InlineData("https://view.zing/abcdefghijklmnopqrstuvwxyz012345")]
	[InlineData("https://app.zing:443/index.html")]
	public void Zings_own_documents_may_load(string url)
	{
		Assert.True(ZingOrigin.IsRendererDocument(url));
	}

	[Theory]
	[InlineData("http://127.0.0.1:8080/")]
	[InlineData("https://example.com/")]
	[InlineData("http://app.zing/index.html")]
	[InlineData("https://app.zing:444/index.html")]
	[InlineData("https://app.zing.example.com/")]
	[InlineData("https://view.zing.example.com/")]
	[InlineData("https://app.zing@example.com/")]
	[InlineData("https://user@app.zing/")]
	[InlineData("https://example.com/https://app.zing/")]
	[InlineData("about:blank")]
	[InlineData("javascript:alert(1)")]
	[InlineData("data:text/html,hi")]
	[InlineData("file:///C:/Windows/win.ini")]
	[InlineData("app.zing/index.html")]
	[InlineData("")]
	public void Anything_else_is_refused(string url)
	{
		Assert.False(ZingOrigin.IsRendererDocument(url));
	}
}

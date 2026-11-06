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

	private const string Home = "https://app.zing/index.html#surface";

	[Fact]
	public void A_view_loads_its_own_page_once_and_again_only_as_a_reload()
	{
		Assert.True(ZingOrigin.MayLoadAsView(Home, Home, first: true, reload: false));
		Assert.True(ZingOrigin.MayLoadAsView(Home, Home, first: false, reload: true));
		Assert.False(ZingOrigin.MayLoadAsView(Home, Home, first: false, reload: false));
	}

	[Theory]
	[InlineData("https://view.zing/abcdefghijklmnopqrstuvwxyz012345")]
	[InlineData("https://app.zing/index.html?again#surface")]
	[InlineData("https://app.zing/index.html#chrome")]
	[InlineData("https://example.com/")]
	public void No_other_page_becomes_the_view_even_first_or_as_a_reload(string url)
	{
		Assert.False(ZingOrigin.MayLoadAsView(Home, url, first: true, reload: true));
	}

	[Theory]
	[InlineData("https://view.zing/abcdefghijklmnopqrstuvwxyz012345", true)]
	[InlineData("https://VIEW.ZING/abc", true)]
	[InlineData("https://app.zing/index.html#surface", false)]
	[InlineData("https://view.zing:444/abc", false)]
	[InlineData("https://view.zing.example.com/abc", false)]
	[InlineData("http://view.zing/abc", false)]
	[InlineData("about:blank", false)]
	public void A_frame_in_a_view_may_load_only_the_viewers_files(string url, bool expected)
	{
		Assert.Equal(expected, ZingOrigin.IsViewerDocument(url));
	}
}

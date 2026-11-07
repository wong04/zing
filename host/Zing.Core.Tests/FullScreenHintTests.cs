using Zing.Core;
using Zing.Core.Net;

namespace Zing.Core.Tests;

public sealed class FullScreenHintTests
{
	[Theory]
	[InlineData("https://video.example/watch?v=1", "video.example is now full screen · Press Esc to exit")]
	[InlineData("http://127.0.0.1:8080/x", "127.0.0.1:8080 is now full screen · Press Esc to exit")]
	[InlineData("zing://home", "This page is now full screen · Press Esc to exit")]
	public void Names_the_site_and_says_how_to_leave(string url, string text)
	{
		Assert.Equal(text, FullScreenHint.Text(url));
	}

	[Fact]
	public void A_relayed_page_is_named_by_the_site_it_stands_for()
	{
		var relayed = RelayedContent.Encode(new Uri("https://news.example/a"))!;
		Assert.Equal("news.example is now full screen · Press Esc to exit", FullScreenHint.Text(relayed.AbsoluteUri));
	}
}

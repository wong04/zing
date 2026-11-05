using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

public sealed class EngineArgumentsTests
{
	[Theory]
	[InlineData("-rf.mov")]
	[InlineData("--version")]
	[InlineData("-")]
	public void A_name_that_looks_like_an_option_becomes_an_absolute_path(string name)
	{
		var path = EngineArguments.FilePath(name);

		Assert.True(Path.IsPathFullyQualified(path));
		Assert.False(path.StartsWith('-'));
		Assert.EndsWith(name, path);
	}

	[Fact]
	public void An_absolute_path_is_unchanged()
	{
		var path = Path.Combine(Path.GetTempPath(), "my film é (1).heic");

		Assert.Equal(path, EngineArguments.FilePath(path));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("a\0b")]
	public void A_path_that_cant_name_a_file_is_refused(string path)
	{
		Assert.Throws<UnsafeEngineArgumentException>(() => EngineArguments.FilePath(path));
	}

	[Fact]
	public void Urls_come_after_a_double_dash_so_none_can_be_an_option()
	{
		Assert.Equal(["--", "https://example.com/a?b=1", "http://example.com/"], EngineArguments.Urls(["https://example.com/a?b=1", "http://example.com/"]));
	}

	[Theory]
	[InlineData("-o")]
	[InlineData("--exec=calc")]
	[InlineData("file:///C:/secret.txt")]
	[InlineData("javascript:alert(1)")]
	[InlineData("example.com")]
	[InlineData("")]
	public void A_url_that_is_not_http_or_https_is_refused(string url)
	{
		Assert.Throws<UnsafeEngineArgumentException>(() => EngineArguments.Urls([url]));
	}
}

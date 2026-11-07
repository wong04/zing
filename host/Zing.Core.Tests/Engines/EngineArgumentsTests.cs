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

	private static IEnumerable<string> Vector(string name) => Vectors.Load("src/shared/convert/mediaUrl.vectors.json").GetProperty(name).EnumerateArray().Select(e => e.GetString()!);

	private static TheoryData<string> Data(IEnumerable<string> values)
	{
		var data = new TheoryData<string>();
		foreach (var value in values) data.Add(value);
		return data;
	}

	public static TheoryData<string> LocalAddresses() => Data(Vector("refuse"));

	public static TheoryData<string> PublicAddresses() => Data(Vector("accept"));

	[Theory]
	[MemberData(nameof(LocalAddresses))]
	public void An_address_on_this_pc_or_the_local_network_is_refused_whatever_the_renderer_checked(string url)
	{
		Assert.Throws<UnsafeEngineArgumentException>(() => EngineArguments.Urls([url]));
	}

	[Theory]
	[MemberData(nameof(PublicAddresses))]
	public void A_public_address_passes(string url)
	{
		Assert.Equal(["--", url], EngineArguments.Urls([url]));
	}

	[Fact]
	public void The_engine_gets_a_host_in_ascii_the_form_that_was_checked()
	{
		Assert.Equal(["--", "https://www.example.com/a", "https://xn--bcher-kva.example/a"], EngineArguments.Urls(["https://ｗｗｗ.example.com/a", "https://bücher.example/a"]));
	}

	[Fact]
	public void The_engine_gets_the_text_that_was_checked_not_the_text_that_was_sent()
	{
		Assert.Equal(["--", "https://example.com/", "http://127.0.0.1.example.com/a"], EngineArguments.Urls(["https://EXAMPLE.com", "http://127.0.0.1.example.com/a"]));
	}

	[Theory]
	[InlineData("http://example.com/a\b")]
	[InlineData("http://exa%6dple.com/")]
	public void A_backslash_or_a_percent_in_the_host_is_refused_because_other_parsers_read_them_differently(string url)
	{
		Assert.Throws<UnsafeEngineArgumentException>(() => EngineArguments.Urls([url]));
	}

	[Theory]
	[InlineData("https://user:secret@example.com/a")]
	[InlineData("https://example.com/a\nhttps://example.org/")]
	[InlineData("https://example.com/a b")]
	public void A_sign_in_in_the_address_or_a_break_in_it_is_refused(string url)
	{
		Assert.Throws<UnsafeEngineArgumentException>(() => EngineArguments.Urls([url]));
	}
}

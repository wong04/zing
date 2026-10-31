using Zing.Core;

namespace Zing.Core.Tests;

public sealed class AddressTests
{
	private static readonly Address Address = Address.FromDefaults();

	[Theory]
	[InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
	[InlineData("example.com", "https://example.com/")]
	[InlineData("zing://home/", "zing://home/")]
	[InlineData("  example.com/path  ", "https://example.com/path")]
	public void Addresses_are_opened(string typed, string expected) => Assert.Equal(expected, Address.Resolve(typed, "google"));

	[Theory]
	[InlineData("google", "https://www.google.com/search?q=hello%20world")]
	[InlineData("startpage", "https://www.startpage.com/sp/search?q=hello%20world")]
	[InlineData("kagi", "https://kagi.com/search?q=hello%20world")]
	public void Everything_else_is_searched_on_the_chosen_engine(string engine, string expected) =>
		Assert.Equal(expected, Address.Resolve("hello world", engine));

	[Fact]
	public void The_engines_come_from_the_typescript_table() =>
		Assert.Equal(6, ZingJson.LoadDefaults("search-engines.json").AsObject().Count);

	[Fact]
	public void An_unknown_engine_is_an_error() =>
		Assert.Throws<Address.UnknownSearchEngineException>(() => Address.Resolve("hello world", "altavista"));
}

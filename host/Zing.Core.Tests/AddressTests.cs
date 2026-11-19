using System.Text.Json;

namespace Zing.Core.Tests;

public sealed class AddressTests
{
	private const string VectorsPath = "src/shared/browser/addressInput.vectors.json";

	private static readonly Address Address = Address.FromDefaults();

	private static JsonElement Section(string name) => Vectors.Load(VectorsPath).GetProperty(name);

	public static TheoryData<string, string> Urls() => Pairs("urls");

	public static TheoryData<string> Searches()
	{
		var data = new TheoryData<string>();
		foreach (var text in Section("searches").EnumerateArray()) data.Add(text.GetString()!);
		return data;
	}

	public static TheoryData<string> Blanks()
	{
		var data = new TheoryData<string>();
		foreach (var text in Section("blank").EnumerateArray()) data.Add(text.GetString()!);
		return data;
	}

	public static TheoryData<string, string, string?> Resolves()
	{
		var data = new TheoryData<string, string, string?>();
		foreach (var v in Section("resolve").EnumerateArray())
			data.Add(v.GetProperty("input").GetString()!, v.GetProperty("engine").GetString()!, v.GetProperty("expected").GetString());
		return data;
	}

	private static TheoryData<string, string> Pairs(string section)
	{
		var data = new TheoryData<string, string>();
		foreach (var pair in Section(section).EnumerateArray()) data.Add(pair[0].GetString()!, pair[1].GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(Urls))]
	public void Typed_places_are_opened(string typed, string expected) =>
		Assert.Equal(new AddressInputResult.Url(expected), AddressInput.Classify(typed));

	[Theory]
	[MemberData(nameof(Searches))]
	public void Typed_words_are_searched(string typed) =>
		Assert.Equal(new AddressInputResult.Search(typed), AddressInput.Classify(typed));

	[Theory]
	[MemberData(nameof(Blanks))]
	public void Blank_input_is_nothing(string typed) => Assert.Null(AddressInput.Classify(typed));

	[Theory]
	[MemberData(nameof(Resolves))]
	public void Typed_text_resolves_to_what_the_engine_or_site_loads(string typed, string engine, string? expected)
	{
		if (expected is null) {
			Assert.Throws<Address.BlankAddressException>(() => Address.Resolve(typed, engine));
			return;
		}
		Assert.Equal(expected, Address.Resolve(typed, engine));
	}

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

	[Theory]
	[InlineData("0.0.0.0", true)]
	[InlineData("255.255.255.255", true)]
	[InlineData("256.1.1.1", false)]
	[InlineData("1.2.3", false)]
	[InlineData("1.2.3.x", false)]
	[InlineData("1..3.4", false)]
	public void IPv4_addresses_are_recognised(string host, bool expected) => Assert.Equal(expected, AddressInput.IsIPv4(host));

	// The address bar's list is the Public Suffix List's, so a top-level domain that came after the hand-kept list still opens.
	[Theory]
	[InlineData("remix.run", "https://remix.run")]
	[InlineData("astro.build", "https://astro.build")]
	[InlineData("val.town/v/x", "https://val.town/v/x")]
	[InlineData("example.museum", "https://example.museum")]
	[InlineData("nic.xn--p1ai", "https://nic.xn--p1ai")]
	[InlineData("пример.рф", "https://пример.рф")]
	[InlineData("printer.local", "https://printer.local")]
	[InlineData("router.home", "https://router.home")]
	public void A_name_under_any_real_top_level_domain_is_opened(string typed, string expected) =>
		Assert.Equal(new AddressInputResult.Url(expected), AddressInput.Classify(typed));

	[Theory]
	[InlineData("node.js")]
	[InlineData("setup.exe")]
	[InlineData("notes.txt")]
	[InlineData("main.cpp")]
	public void A_file_name_whose_extension_is_no_top_level_domain_is_searched(string typed) =>
		Assert.Equal(new AddressInputResult.Search(typed), AddressInput.Classify(typed));
}

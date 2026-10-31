using System.Text.Json;

namespace Zing.Core.Tests;

public sealed class LinkCleanerTests
{
	private const string VectorsPath = "src/shared/browser/linkCleaner.vectors.json";

	private static JsonElement Section(string name) => Vectors.Load(VectorsPath).GetProperty(name);

	public static TheoryData<string, string, string[]> Cleans()
	{
		var data = new TheoryData<string, string, string[]>();
		foreach (var v in Section("clean").EnumerateArray())
			data.Add(v.GetProperty("input").GetString()!, v.GetProperty("url").GetString()!, [.. v.GetProperty("removed").EnumerateArray().Select(r => r.GetString()!)]);
		return data;
	}

	public static TheoryData<string> Invalids()
	{
		var data = new TheoryData<string>();
		foreach (var v in Section("invalid").EnumerateArray()) data.Add(v.GetProperty("input").GetString()!);
		return data;
	}

	public static TheoryData<string, string> Strips()
	{
		var data = new TheoryData<string, string>();
		foreach (var v in Section("strip").EnumerateArray())
			data.Add(v.GetProperty("input").GetString()!, v.GetProperty("expected").GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(Cleans))]
	public void A_link_loses_only_its_trackers(string input, string url, string[] removed)
	{
		var cleaned = LinkCleaner.CleanLink(input);
		Assert.Equal(url, cleaned.Url);
		Assert.Equal(removed, cleaned.Removed);
	}

	[Theory]
	[MemberData(nameof(Invalids))]
	public void Something_that_is_not_a_link_is_refused(string input) =>
		Assert.Throws<InvalidLinkException>(() => LinkCleaner.CleanLink(input));

	[Theory]
	[MemberData(nameof(Strips))]
	public void Navigation_strips_trackers_from_http_addresses_only(string input, string expected) =>
		Assert.Equal(expected, LinkCleaner.StripTrackers(input));
}

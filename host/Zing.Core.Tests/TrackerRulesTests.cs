using System.Text.Json;

namespace Zing.Core.Tests;

/// <summary>src/shared/browser/trackerRules.vectors.json, which the TypeScript list is held to as well.</summary>
public sealed class TrackerRulesTests
{
	private const string VectorsPath = "src/shared/browser/trackerRules.vectors.json";

	private static JsonElement Section(string name) => Vectors.Load(VectorsPath).GetProperty(name);

	public static TheoryData<string, bool> Hosts()
	{
		var data = new TheoryData<string, bool>();
		foreach (var v in Section("hosts").EnumerateArray()) data.Add(v.GetProperty("host").GetString()!, v.GetProperty("tracker").GetBoolean());
		return data;
	}

	public static TheoryData<string, string, bool> Requests()
	{
		var data = new TheoryData<string, string, bool>();
		foreach (var v in Section("requests").EnumerateArray())
			data.Add(v.GetProperty("request").GetString()!, v.GetProperty("page").GetString()!, v.GetProperty("blocked").GetBoolean());
		return data;
	}

	[Fact]
	public void The_list_is_the_one_the_settings_page_counts() =>
		Assert.Equal(Section("domainCount").GetInt32(), TrackerRules.Domains.Count);

	[Theory]
	[MemberData(nameof(Hosts))]
	public void A_host_is_a_tracker_only_if_listed_or_under_a_listed_domain(string host, bool tracker) =>
		Assert.Equal(tracker, TrackerRules.IsTrackerHost(host));

	[Theory]
	[MemberData(nameof(Requests))]
	public void Only_third_party_web_requests_to_trackers_are_blocked(string request, string page, bool blocked) =>
		Assert.Equal(blocked, TrackerRules.Blocks(request, page));
}

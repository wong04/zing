using System.Diagnostics;
using System.Text.Json;

namespace Zing.Core.Tests;

/// <summary>
/// The C# twin of src/shared/security/threatModel.ts (URL part), held to it by threatModel.vectors.json, so the
/// 16 must-be-clean sites of Krackn and the rest are checked by both.
/// </summary>
public sealed class ThreatModelTests
{
	private const string VectorsPath = "src/shared/security/threatModel.vectors.json";

	private static JsonElement Section(string name) => Vectors.Load(VectorsPath).GetProperty(name);

	public static TheoryData<string> Clean()
	{
		var data = new TheoryData<string>();
		foreach (var url in Section("mustBeClean").EnumerateArray()) data.Add(url.GetString()!);
		return data;
	}

	public static TheoryData<string> Verdicts()
	{
		var data = new TheoryData<string>();
		foreach (var v in Section("verdicts").EnumerateArray()) data.Add(v.GetRawText());
		return data;
	}

	public static TheoryData<string> Hosts()
	{
		var data = new TheoryData<string>();
		foreach (var v in Section("hostFindings").EnumerateArray()) data.Add(v.GetRawText());
		return data;
	}

	private static string[] Titles(IEnumerable<ThreatFinding> findings) => [.. findings.Select(f => f.Title)];

	[Theory]
	[MemberData(nameof(Clean))]
	public void A_real_site_has_no_findings(string url) => Assert.Empty(ThreatModel.InspectUrl(new Uri(url)).Findings);

	[Theory]
	[MemberData(nameof(Verdicts))]
	public void A_verdict_is_the_one_the_TS_gives(string json)
	{
		var v = JsonDocument.Parse(json).RootElement;
		var verdict = ThreatModel.InspectUrl(new Uri(v.GetProperty("url").GetString()!));
		Assert.Equal(v.GetProperty("level").GetInt32(), (int)verdict.Level);
		Assert.Equal(v.GetProperty("titles").EnumerateArray().Select(t => t.GetString()!), Titles(verdict.Findings));
	}

	[Theory]
	[MemberData(nameof(Hosts))]
	public void A_host_has_the_findings_the_TS_gives(string json)
	{
		var v = JsonDocument.Parse(json).RootElement;
		var findings = ThreatModel.InspectHost(v.GetProperty("host").GetString()!, v.GetProperty("scheme").GetString()!);
		Assert.Equal(v.GetProperty("titles").EnumerateArray().Select(t => t.GetString()!), Titles(findings));
	}

	[Fact]
	public void The_reason_names_the_real_domain_behind_a_brand()
	{
		var finding = ThreatModel.InspectHost("apple.com.evil.tk", "https").Single(f => f.Level == ThreatLevel.Dangerous);
		Assert.Contains("evil.tk", finding.Detail);
	}

	[Fact]
	public void Ten_thousand_inspections_take_under_a_hundred_milliseconds()
	{
		var urls = new[] { "https://www.google.com/search?q=hello", "https://apple.com.secure-login.tk/verify", "https://login.microsoftonline.com/common/oauth2", "http://185.243.115.24/payload", "https://a.b.c.d.e.f.example.org/" }
			.Select(u => new Uri(u)).ToArray();
		for (var i = 0; i < 1_000; i++) ThreatModel.InspectUrl(urls[i % urls.Length]);
		// Best of three, so one run that lands on a busy machine doesn't fail the test.
		var best = Enumerable.Range(0, 3).Select(_ =>
		{
			var clock = Stopwatch.StartNew();
			for (var i = 0; i < 10_000; i++) ThreatModel.InspectUrl(urls[i % urls.Length]);
			return clock.Elapsed.TotalMilliseconds;
		}).Min();
		Assert.True(best < 100, $"10,000 inspections took {best:F1} ms at best");
	}
}

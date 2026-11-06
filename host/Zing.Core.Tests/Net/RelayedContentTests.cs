using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class RelayedContentTests
{
	private static JsonElement Cases(string name) => Vectors.Load("src/shared/unblock/relayedWebContent.vectors.json").GetProperty(name);

	public static TheoryData<string, string> Mappings() => Pairs("mapping", "web", "relay");

	private static TheoryData<string, string> Pairs(string name, string a, string b)
	{
		var data = new TheoryData<string, string>();
		foreach (var c in Cases(name).EnumerateArray()) data.Add(c.GetProperty(a).GetString()!, c.GetProperty(b).GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(Mappings))]
	public void MapsBothWays(string web, string relay)
	{
		Assert.Equal(relay, RelayedContent.Encode(new Uri(web))?.AbsoluteUri);
		Assert.Equal(web, RelayedContent.Decode(relay)?.AbsoluteUri);
	}

	[Fact]
	public void OnlyHttpsIsEncoded()
	{
		foreach (var web in Cases("notEncoded").EnumerateArray()) Assert.Null(RelayedContent.Encode(new Uri(web.GetString()!)));
	}

	[Fact]
	public void OnlyTheRelaySchemeIsDecoded()
	{
		foreach (var other in Cases("notDecoded").EnumerateArray()) Assert.Null(RelayedContent.Decode(other.GetString()!));
	}

	[Fact]
	public void PatchesTextAsTheTypeScriptDoes()
	{
		foreach (var c in Cases("patchText").EnumerateArray())
			Assert.Equal(c.GetProperty("out").GetString(), RelayedContent.PatchText(c.GetProperty("in").GetString()!));
	}

	[Fact]
	public void FindsTheCharsetAsTheTypeScriptDoes()
	{
		foreach (var c in Cases("charset").EnumerateArray())
			Assert.Equal(c.GetProperty("charset").GetString(), RelayedContent.Charset(c.GetProperty("contentType").GetString()!));
	}

	[Fact]
	public void PatchesBytesAsTheTypeScriptDoes()
	{
		foreach (var c in Cases("patch").EnumerateArray())
		{
			var patched = RelayedContent.Patch(Convert.FromHexString(c.GetProperty("bodyHex").GetString()!), c.GetProperty("contentType").GetString()!);
			Assert.Equal(c.GetProperty("outHex").GetString(), Convert.ToHexString(patched).ToLowerInvariant());
		}
	}

	[Fact]
	public void AppLikeFailureAsTheTypeScriptJudges()
	{
		foreach (var c in Cases("appLike").EnumerateArray())
			Assert.Equal(c.GetProperty("failure").GetBoolean(), RelayedContent.IsAppLikeFailure(c.GetProperty("textLength").GetInt32(), c.GetProperty("scripts").GetInt32()));
	}

	public static TheoryData<string> HostileBodies() => new()
	{
		"<base",
		"<link rel=prefetch",
		"<link ",
		"src" + new string(' ', 1000),
		"url(" + new string(' ', 1000),
		", ",
	};

	[Theory]
	[MemberData(nameof(HostileBodies))]
	public async Task A_hostile_16_MB_body_is_patched_in_bounded_time(string unit)
	{
		var body = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(unit, 16 * 1024 * 1024 / unit.Length)));
		var patched = await Task.Run(() => RelayedContent.Patch(body, "text/html"), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
		Assert.Equal(body.Length, patched.Length);
	}

	[Fact]
	public void ToWebAddressDecodesRelayUrlsAndLeavesOthers()
	{
		Assert.Equal("https://a.com/x", RelayedContent.ToWebAddress("zing-relay://a.com/x"));
		Assert.Equal("http://a.com/x", RelayedContent.ToWebAddress("http://a.com/x"));
	}
}

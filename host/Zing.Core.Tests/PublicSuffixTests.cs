using System.Globalization;
using System.Text.RegularExpressions;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>The Public Suffix List's own test cases (vectors/test_psl.txt, from publicsuffix/list), plus the hosts Zing cares about.</summary>
public sealed partial class PublicSuffixTests
{
	[GeneratedRegex(@"^checkPublicSuffix\((null|'[^']*'), (null|'[^']*')\);$")]
	private static partial Regex Check();

	private static readonly IdnMapping Idn = new();

	public static TheoryData<string, string> OfficialCases()
	{
		var data = new TheoryData<string, string>();
		foreach (var line in Vectors.Text("host/Zing.Core.Tests/vectors/test_psl.txt").Split('\n'))
		{
			var match = Check().Match(line.Trim());
			if (!match.Success) continue;
			data.Add(match.Groups[1].Value, match.Groups[2].Value);
		}
		return data;
	}

	[Theory]
	[MemberData(nameof(OfficialCases))]
	public void The_registrable_domain_follows_the_lists_own_tests(string input, string expected)
	{
		// A null host isn't something a Uri ever gives; the list's own test of it has nothing to check here.
		if (input == "null") return;
		var want = expected == "null" ? null : Idn.GetAscii(expected.Trim('\''));
		Assert.Equal(want, PublicSuffix.RegistrableDomain(input.Trim('\'')));
	}

	[Fact]
	public void The_official_cases_were_all_read()
	{
		var active = Vectors.Text("host/Zing.Core.Tests/vectors/test_psl.txt").Split('\n').Count(l => l.StartsWith("checkPublicSuffix(", StringComparison.Ordinal));
		Assert.Equal(active, OfficialCases().Count);
	}

	[Theory]
	[InlineData("alice.github.io", "alice.github.io")]
	[InlineData("github.io", null)]
	[InlineData("auth.bank.com", "bank.com")]
	[InlineData("uct.ac.za", "uct.ac.za")]
	[InlineData("login.microsoftonline.com", "microsoftonline.com")]
	[InlineData("www.example.co.uk.", "example.co.uk")]
	[InlineData("127.0.0.1", "127.0.0.1")]
	[InlineData("xn--a.attacker.example", "attacker.example")]
	public void Shared_hosts_are_their_own_sites_and_addresses_are_themselves(string host, string? expected)
	{
		Assert.Equal(expected, PublicSuffix.RegistrableDomain(host));
	}

	[Fact]
	public void An_IPv6_host_as_a_Uri_gives_it_is_itself()
	{
		var host = new Uri("http://[::1]:3000/").IdnHost;
		Assert.Equal(host, PublicSuffix.RegistrableDomain(host));
	}
}

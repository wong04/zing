using System.Diagnostics;
using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class DnsScriptTests
{
	private static DnsAdapter Dhcp(string name, bool v6 = true) => new(name, [], v6 ? [] : null);

	private static readonly IReadOnlyList<DohEntry> NoDoh = [];

	[Fact]
	public void Apply_names_only_the_adapters_it_is_given()
	{
		var script = DnsScript.Apply("cloudflare", [Dhcp("Ethernet"), Dhcp("Wi-Fi")]);
		Assert.Contains("-ceq 'Ethernet'", script);
		Assert.Contains("-ceq 'Wi-Fi'", script);
		Assert.DoesNotContain("Bluetooth", script);
		Assert.Equal(2, CountOf(script, "Set-DnsClientServerAddress"));
	}

	[Fact]
	public void Apply_sets_the_providers_servers_and_turns_encryption_on_for_them()
	{
		var script = DnsScript.Apply("google", [Dhcp("Ethernet")]);
		foreach (var address in new[] { "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844" })
			Assert.Contains($"'{address}'", script);
		Assert.Contains("'https://dns.google/dns-query'", script);
		// Registered servers with AutoUpgrade off stay plain DNS, which is how Windows ships them.
		Assert.Contains("-AutoUpgrade $true", script);
		Assert.Contains("-AllowFallbackToUdp $false", script);
		Assert.Contains("Set-DnsClientDohServerAddress", script);
		Assert.Contains("Add-DnsClientDohServerAddress", script);
	}

	[Fact]
	public void Apply_leaves_ipv6_servers_off_an_adapter_without_ipv6()
	{
		var script = DnsScript.Apply("cloudflare", [Dhcp("Ethernet", v6: false)]);
		Assert.Contains("-ServerAddresses '1.1.1.1', '1.0.0.1'\n", script);
		Assert.DoesNotContain("-ServerAddresses '1.1.1.1', '1.0.0.1', '2606", script);
	}

	[Fact]
	public void Apply_gives_an_adapter_with_ipv6_both_families_in_one_call()
	{
		var script = DnsScript.Apply("cloudflare", [Dhcp("Ethernet")]);
		Assert.Contains("-ServerAddresses '1.1.1.1', '1.0.0.1', '2606:4700:4700::1111', '2606:4700:4700::1001'", script);
	}

	[Fact]
	public void Apply_stops_with_a_code_of_its_own_when_this_windows_has_no_encrypted_dns()
	{
		var script = DnsScript.Apply("cloudflare", [Dhcp("Ethernet")]);
		var guard = script.IndexOf("Get-Command Add-DnsClientDohServerAddress", StringComparison.Ordinal);
		Assert.InRange(guard, 0, script.IndexOf("Set-DnsClientServerAddress", StringComparison.Ordinal));
		Assert.Contains($"exit {DnsScript.NoEncryptedDnsExit}", script);
	}

	[Fact]
	public void Apply_quotes_a_name_with_a_quote_in_it()
	{
		var script = DnsScript.Apply("cloudflare", [Dhcp("Bob's LAN")]);
		Assert.Contains("-ceq 'Bob''s LAN'", script);
	}

	[Fact]
	public void Apply_doubles_the_curly_quotes_powershell_also_treats_as_quotes()
	{
		var script = DnsScript.Apply("cloudflare", [Dhcp("a‘b’c‚d‛e")]);
		Assert.Contains("'a‘‘b’’c‚‚d‛‛e'", script);
	}

	public static TheoryData<string> HostileNames() => new()
	{
		"x'; calc; '",
		"x’; calc; ‘",
		"line one\nline two; calc",
		"back`tick`n",
		"$(calc) $env:USERNAME ${x}",
		"a;b|c&d",
		"'",
		"''",
		"x' -ServerAddresses '6.6.6.6",
		"<# comment #>",
		"# not a comment",
		"@'\nhere\n'@",
	};

	[Theory]
	[MemberData(nameof(HostileNames))]
	public void A_hostile_adapter_name_stays_one_string_in_apply(string name)
	{
		var shape = ParseWithPowerShell(DnsScript.Apply("quad9", [Dhcp(name)]));
		AssertOnlyOurCommands(shape, name);
	}

	[Theory]
	[MemberData(nameof(HostileNames))]
	public void A_hostile_adapter_name_stays_one_string_in_undo(string name)
	{
		var shape = ParseWithPowerShell(DnsScript.Undo([new DnsAdapter(name, ["10.0.0.1"], ["fd00::1"])], NoDoh, "quad9"));
		AssertOnlyOurCommands(shape, name);
	}

	[Fact]
	public void A_hostile_name_comes_back_exactly_as_the_adapter_was_called()
	{
		const string name = "x'; calc; ’\n`$(calc)";
		var shape = ParseWithPowerShell(DnsScript.Apply("cloudflare", [Dhcp(name)]));
		Assert.Contains(name, shape.Strings);
	}

	[Theory]
	[InlineData("1.1.1.1'; calc; '")]
	[InlineData("not an address")]
	[InlineData("")]
	[InlineData("1.1.1.1\n2.2.2.2")]
	public void An_address_that_is_not_an_address_is_refused(string address)
	{
		Assert.Throws<InvalidDnsAddressError>(() => DnsScript.Undo([new DnsAdapter("Ethernet", [address], [])], NoDoh, "cloudflare"));
		Assert.Throws<InvalidDnsAddressError>(() => DnsScript.Undo([new DnsAdapter("Ethernet", [], [address])], NoDoh, "cloudflare"));
	}

	[Fact]
	public void An_address_of_the_wrong_family_is_refused()
	{
		Assert.Throws<InvalidDnsAddressError>(() => DnsScript.Undo([new DnsAdapter("Ethernet", ["fd00::1"], [])], NoDoh, "cloudflare"));
		Assert.Throws<InvalidDnsAddressError>(() => DnsScript.Undo([new DnsAdapter("Ethernet", [], ["10.0.0.1"])], NoDoh, "cloudflare"));
	}

	[Fact]
	public void A_provider_outside_the_fixed_list_is_refused()
	{
		Assert.Throws<UnknownDohProviderError>(() => DnsScript.Apply("evil'; calc", [Dhcp("Ethernet")]));
		Assert.Throws<UnknownDohProviderError>(() => DnsScript.Undo([Dhcp("Ethernet")], NoDoh, "evil"));
	}

	[Fact]
	public void Apply_with_no_adapters_is_refused()
	{
		Assert.Throws<NoDnsAdaptersError>(() => DnsScript.Apply("cloudflare", []));
	}

	[Fact]
	public void Undo_puts_back_static_servers_and_leaves_dhcp_adapters_automatic()
	{
		var script = DnsScript.Undo([new DnsAdapter("Ethernet", ["10.0.0.1", "10.0.0.2"], ["fd00::1"]), Dhcp("Wi-Fi", v6: false)], NoDoh, "cloudflare");
		Assert.Contains("-ServerAddresses '10.0.0.1', '10.0.0.2', 'fd00::1'", script);
		Assert.Equal(2, CountOf(script, "-ResetServerAddresses"));
		Assert.Equal(1, CountOf(script, "-ServerAddresses"));
	}

	[Fact]
	public void Undo_resets_before_it_sets_so_the_providers_servers_do_not_linger()
	{
		var script = DnsScript.Undo([new DnsAdapter("Ethernet", ["10.0.0.1"], [])], NoDoh, "cloudflare");
		var reset = script.IndexOf("-ResetServerAddresses", StringComparison.Ordinal);
		var set = script.IndexOf("-ServerAddresses '10.0.0.1'", StringComparison.Ordinal);
		Assert.InRange(reset, 0, set);
	}

	[Fact]
	public void Undo_skips_an_adapter_that_is_gone_instead_of_failing_the_whole_undo()
	{
		var script = DnsScript.Undo([Dhcp("Unplugged")], NoDoh, "cloudflare");
		Assert.Contains("-ceq 'Unplugged'", script);
		Assert.Contains("if ($adapter)", script);
	}

	[Fact]
	public void Undo_restores_the_encrypted_dns_entries_as_they_were_and_removes_the_ones_it_added()
	{
		var was = new DohEntry("1.1.1.1", "https://cloudflare-dns.com/dns-query", false, false);
		var script = DnsScript.Undo([Dhcp("Ethernet")], [was], "cloudflare");
		Assert.Contains("Set-DnsClientDohServerAddress -ServerAddress '1.1.1.1' -DohTemplate 'https://cloudflare-dns.com/dns-query' -AllowFallbackToUdp $false -AutoUpgrade $false", script);
		Assert.Contains("Remove-DnsClientDohServerAddress -ServerAddress '1.0.0.1'", script);
		Assert.DoesNotContain("Remove-DnsClientDohServerAddress -ServerAddress '1.1.1.1'", script);
	}

	[Fact]
	public void Undo_refuses_a_template_that_is_not_an_https_address()
	{
		Assert.Throws<InvalidDohTemplateError>(() => DnsScript.Undo([Dhcp("Ethernet")], [new DohEntry("1.1.1.1", "x'; calc; '", false, false)], "cloudflare"));
		Assert.Throws<InvalidDohTemplateError>(() => DnsScript.Undo([Dhcp("Ethernet")], [new DohEntry("1.1.1.1", "http://cloudflare-dns.com/dns-query", false, false)], "cloudflare"));
	}

	[Fact]
	public void Undo_refuses_an_entry_for_a_server_the_provider_does_not_own()
	{
		Assert.Throws<InvalidDnsAddressError>(() => DnsScript.Undo([Dhcp("Ethernet")], [new DohEntry("9.9.9.9", "https://dns.quad9.net/dns-query", false, false)], "cloudflare"));
	}

	[Theory]
	[InlineData("Wi-F*")]
	[InlineData("Ethernet?")]
	[InlineData("Ethernet [2]")]
	[InlineData("*")]
	public void A_name_with_wildcard_characters_matches_only_itself(string name)
	{
		foreach (var script in new[] { DnsScript.Apply("cloudflare", [Dhcp(name)]), DnsScript.Undo([new DnsAdapter(name, ["10.0.0.1"], [])], NoDoh, "cloudflare") })
		{
			// -Name and -InterfaceAlias take wildcards (Get-NetAdapter -Name 'Wi-F*' finds Wi-Fi); -ceq compares exactly.
			Assert.DoesNotContain("-InterfaceAlias", script);
			Assert.DoesNotContain("-Name", script);
			Assert.Contains($"-ceq {DnsScript.Quote(name)}", script);
			Assert.Contains("-InterfaceIndex $adapter.ifIndex", script);
		}
	}

	[Fact]
	public void Apply_stops_when_an_adapter_it_was_shown_is_gone_instead_of_skipping_it()
	{
		var script = DnsScript.Apply("cloudflare", [Dhcp("Ethernet")]);
		Assert.Contains("if (-not $adapter) { throw", script);
	}

	[Fact]
	public void Visible_spells_out_what_would_otherwise_be_hidden_or_rearrange_the_text()
	{
		Assert.Equal("Ethernet", DnsDisplay.Visible("Ethernet"));
		Assert.Equal("a\\u202Eb", DnsDisplay.Visible("a\u202Eb"));
		Assert.Equal("a\\u200Fb\\u2066c\\u061Cd", DnsDisplay.Visible("a\u200Fb\u2066c\u061Cd"));
		Assert.Equal("one\\u000Atwo\\u0000", DnsDisplay.Visible("one\ntwo\0"));
		Assert.Equal("caf\u00E9 \u30A4\u30FC\u30B5", DnsDisplay.Visible("caf\u00E9 \u30A4\u30FC\u30B5"));
	}

	[Fact]
	public void The_script_as_shown_spells_out_hidden_characters_but_keeps_its_own_lines()
	{
		var script = DnsScript.Apply("cloudflare", [Dhcp("a\u202Eb")]);
		var shown = DnsDisplay.VisibleScript(script);
		Assert.Contains("\\u202E", shown);
		Assert.DoesNotContain("\u202E", shown, StringComparison.Ordinal);
		Assert.Equal(script.Count(c => c == '\n'), shown.Count(c => c == '\n'));
	}

	[Fact]
	public void Undo_lists_what_it_will_set_from_the_same_data_the_script_uses()
	{
		var lines = DnsScript.UndoSummary([new DnsAdapter("Ethernet", ["10.0.0.1"], ["fd00::1"]), Dhcp("Wi-Fi", v6: false)], [new DohEntry("1.1.1.1", "https://cloudflare-dns.com/dns-query", false, false)], "cloudflare");
		Assert.Equal("Ethernet: 10.0.0.1, fd00::1", lines[0]);
		Assert.Equal("Wi-Fi: automatic (DHCP)", lines[1]);
		Assert.Contains(lines, l => l.StartsWith("1.1.1.1: restored to", StringComparison.Ordinal) && l.Contains("https://cloudflare-dns.com/dns-query"));
		Assert.Contains("1.0.0.1: removed from Windows' encrypted DNS list", lines);
	}

	[Fact]
	public void The_undo_list_spells_out_hidden_characters_in_a_name()
	{
		var lines = DnsScript.UndoSummary([new DnsAdapter("a\u202Eb", [], [])], NoDoh, "cloudflare");
		Assert.Equal("a\\u202Eb: automatic (DHCP)", lines[0]);
	}

	[Fact]
	public void The_undo_list_spells_out_hidden_characters_in_a_template()
	{
		var shown = DnsDisplay.Visible("https://cloudflare-dns.com/dns‮query");
		Assert.Contains("\u202E", shown);
		Assert.DoesNotContain('‮', shown);
	}

	[Theory]
	[InlineData("https://cloudflare-dns.com/dns‮query")]
	[InlineData("https://cloudflare-dns.com/dns​query")]
	[InlineData("https://cloudflare-dns.com/dns⁦query")]
	public void A_template_with_a_hidden_formatting_character_is_refused(string template)
	{
		Assert.Throws<InvalidDohTemplateError>(() => DnsScript.Undo([Dhcp("Ethernet")], [new DohEntry("1.1.1.1", template, false, false)], "cloudflare"));
		Assert.Throws<InvalidDohTemplateError>(() => DnsAddresses.Template(template));
	}

	[Fact]
	public void The_script_is_sent_as_an_encoded_command_and_one_too_long_for_a_command_line_is_refused()
	{
		var encoded = DnsScript.EncodedCommand("Write-Output 'hi'");
		Assert.Equal("Write-Output 'hi'", System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
		Assert.Throws<DnsScriptTooLongError>(() => DnsScript.EncodedCommand(new string('x', 20000)));
	}

	[Fact]
	public void The_adapter_list_reads_what_the_listing_script_prints()
	{
		const string json = """
			{"adapters":[{"name":"Ethernet","v4":"10.0.0.1,10.0.0.2","v6enabled":true,"v6":""},{"name":"Wi-Fi","v4":null,"v6enabled":false,"v6":null}],"dohSupported":true,
			 "doh":[{"address":"1.1.1.1","template":"https://cloudflare-dns.com/dns-query","allowFallbackToUdp":false,"autoUpgrade":false}]}
			""";
		var snapshot = DnsSnapshot.Parse(json);
		Assert.Equal(["10.0.0.1", "10.0.0.2"], snapshot.Adapters[0].V4);
		Assert.Empty(snapshot.Adapters[0].V6!);
		Assert.Empty(snapshot.Adapters[1].V4);
		Assert.Null(snapshot.Adapters[1].V6);
		Assert.Equal(new DohEntry("1.1.1.1", "https://cloudflare-dns.com/dns-query", false, false), snapshot.Doh[0]);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("{}")]
	[InlineData("""{"adapters":[],"doh":[]}""")]
	[InlineData("""{"adapters":[{"name":"E","v4":"not an ip","v6enabled":false,"v6":null}],"dohSupported":true,"doh":[]}""")]
	[InlineData("""{"adapters":[{"name":"E","v4":null,"v6enabled":false,"v6":null}],"dohSupported":true}""")]
	public void A_listing_that_is_not_what_the_script_prints_is_an_error(string json)
	{
		Assert.Throws<DnsListingError>(() => DnsSnapshot.Parse(json));
	}

	[Fact]
	public void An_odd_entry_for_a_server_that_is_not_a_providers_does_not_break_the_listing()
	{
		const string json = """
			{"adapters":[],"dohSupported":true,"doh":[
			 {"address":"not an address","template":null,"allowFallbackToUdp":false,"autoUpgrade":false},
			 {"address":"203.0.113.9","template":"ftp://x","allowFallbackToUdp":false,"autoUpgrade":false},
			 {"address":"1.1.1.1","template":"https://cloudflare-dns.com/dns-query","allowFallbackToUdp":false,"autoUpgrade":true}]}
			""";
		var snapshot = DnsSnapshot.Parse(json);
		Assert.Equal("1.1.1.1", Assert.Single(snapshot.Doh).Address);
	}

	[Fact]
	public void A_server_written_another_way_is_still_that_server()
	{
		const string json = """{"adapters":[],"dohSupported":true,"doh":[{"address":"2606:4700:4700:0:0:0:0:1111","template":"https://cloudflare-dns.com/dns-query","allowFallbackToUdp":false,"autoUpgrade":true}]}""";
		Assert.Equal("2606:4700:4700::1111", Assert.Single(DnsSnapshot.Parse(json).Doh).Address);
		Assert.True(DnsProviders.IsKnownServer("2606:4700:4700:0000:0000:0000:0000:1001"));
		Assert.True(DnsProviders.IsKnownServer("2620:FE::FE"));
		Assert.False(DnsProviders.IsKnownServer("1"));
		Assert.False(DnsProviders.IsKnownServer("not an address"));
	}

	[Fact]
	public void An_odd_entry_for_a_providers_own_server_is_still_an_error()
	{
		const string json = """{"adapters":[],"dohSupported":true,"doh":[{"address":"1.1.1.1","template":"ftp://x","allowFallbackToUdp":false,"autoUpgrade":false}]}""";
		Assert.Throws<DnsListingError>(() => DnsSnapshot.Parse(json));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void The_listing_says_whether_this_windows_has_encrypted_dns_commands(bool supported)
	{
		var snapshot = DnsSnapshot.Parse($$"""{"adapters":[],"dohSupported":{{(supported ? "true" : "false")}},"doh":[]}""");
		Assert.Equal(supported, snapshot.DohSupported);
	}

	[Fact]
	public void The_listing_script_asks_whether_the_commands_exist_and_still_changes_nothing()
	{
		Assert.Contains("dohSupported", DnsScript.List);
	}

	[Fact]
	public void The_listing_script_changes_nothing()
	{
		var shape = ParseWithPowerShell(DnsScript.List);
		Assert.Empty(shape.Errors);
		Assert.All(shape.Commands, command => Assert.Contains(command, new[] { "Get-NetAdapter", "Where-Object", "Get-NetAdapterBinding", "Get-ItemProperty", "Get-Command", "Get-DnsClientDohServerAddress", "ForEach-Object", "ConvertTo-Json" }));
	}

	private static int CountOf(string text, string needle)
	{
		var count = 0;
		for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal)) count++;
		return count;
	}

	private sealed record ScriptShape(IReadOnlyList<string> Errors, IReadOnlyList<string> Commands, IReadOnlyList<string> Strings);

	/// <summary>
	/// What PowerShell's own parser makes of a script, which it only parses and never runs: a name that got out of its quotes would show up here as a command.
	/// </summary>
	private static ScriptShape ParseWithPowerShell(string script)
	{
		const string parser = """
			[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
			$text = [Environment]::GetEnvironmentVariable('ZING_TEST_SCRIPT')
			$tokens = $null; $errors = $null
			$ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
			$commands = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() }
			$strings = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object { $_.Value }
			ConvertTo-Json -Compress -InputObject @{ errors = @($errors | ForEach-Object { $_.Message }); commands = @($commands); strings = @($strings) }
			""";
		var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
		{
			RedirectStandardOutput = true,
			StandardOutputEncoding = System.Text.Encoding.UTF8,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(parser)) }) start.ArgumentList.Add(argument);
		start.Environment["ZING_TEST_SCRIPT"] = script;
		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
		using var json = JsonDocument.Parse(output);
		List<string> Read(string key) => [.. json.RootElement.GetProperty(key).EnumerateArray().Select(e => e.GetString() ?? "")];
		return new ScriptShape(Read("errors"), Read("commands"), Read("strings"));
	}

	private static readonly string[] AllowedCommands =
	[
		"Get-Command", "Get-DnsClientDohServerAddress", "Set-DnsClientDohServerAddress", "Add-DnsClientDohServerAddress", "Remove-DnsClientDohServerAddress",
		"Set-DnsClientServerAddress", "Get-NetAdapter", "Where-Object", "Clear-DnsClientCache",
	];

	private static void AssertOnlyOurCommands(ScriptShape shape, string name)
	{
		Assert.Empty(shape.Errors);
		Assert.All(shape.Commands, command => Assert.Contains(command, AllowedCommands));
		Assert.Contains(name, shape.Strings);
	}
}

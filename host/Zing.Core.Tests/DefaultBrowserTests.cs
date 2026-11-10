using Zing.Core;

namespace Zing.Core.Tests;

public sealed class DefaultBrowserTests
{
	private const string Exe = @"C:\Users\A B\AppData\Local\ZingBrowser\current\Zing.exe";
	private const string ClassKey = @"Software\Classes\ZingHTML";
	private const string ClientKey = @"Software\Clients\StartMenuInternet\Zing";
	private const string CapabilitiesKey = ClientKey + @"\Capabilities";

	private static string? Get(string key, string? name) =>
		DefaultBrowserRegistration.Values(Exe).Single(v => v.Key == key && v.Name == name).Value;

	[Fact]
	public void Command_quotes_the_exe_and_the_url()
	{
		const string expected = $"\"{Exe}\" \"%1\"";
		Assert.Equal(expected, Get(ClassKey + @"\shell\open\command", null));
		Assert.Equal(expected, Get(ClientKey + @"\shell\open\command", null));
	}

	[Fact]
	public void Every_association_points_at_the_prog_id()
	{
		foreach (var name in new[] { "http", "https" }) Assert.Equal("ZingHTML", Get(CapabilitiesKey + @"\URLAssociations", name));
		foreach (var name in new[] { ".htm", ".html" }) Assert.Equal("ZingHTML", Get(CapabilitiesKey + @"\FileAssociations", name));
		Assert.Equal("Zing", Get(ClientKey, null));
		Assert.False(string.IsNullOrWhiteSpace(Get(CapabilitiesKey, "ApplicationName")));
		Assert.False(string.IsNullOrWhiteSpace(Get(CapabilitiesKey, "ApplicationDescription")));
		Assert.Equal(Exe + ",0", Get(ClassKey + @"\DefaultIcon", null));
		Assert.Equal(Exe + ",0", Get(ClientKey + @"\DefaultIcon", null));
	}

	[Fact]
	public void Registered_applications_points_at_capabilities()
	{
		Assert.Equal(CapabilitiesKey, Get(@"Software\RegisteredApplications", "Zing"));
	}

	[Fact]
	public void Capabilities_name_the_icon_and_the_start_menu_entry()
	{
		Assert.Equal(Exe + ",0", Get(CapabilitiesKey, "ApplicationIcon"));
		Assert.Equal("Zing", Get(CapabilitiesKey + @"\Startmenu", "StartMenuInternet"));
	}

	[Theory]
	[InlineData(".htm")]
	[InlineData(".html")]
	public void Open_with_lists_zing_for_html_extensions(string extension)
	{
		Assert.Equal("", Get($@"Software\Classes\{extension}\OpenWithProgids", "ZingHTML"));
	}

	[Fact]
	public void Named_values_to_delete_are_exactly_the_shared_key_values_zing_writes()
	{
		var named = DefaultBrowserRegistration.NamedValuesToDelete();
		Assert.Equal(3, named.Count);
		var written = DefaultBrowserRegistration.Values(Exe);
		Assert.All(named, n => Assert.Contains(written, w => w.Key == n.Key && w.Name == n.Name));
		Assert.Contains(named, n => n.Key == @"Software\RegisteredApplications" && n.Name == "Zing");
		Assert.DoesNotContain(named, n => n.Key.StartsWith(@"Software\Classes\ZingHTML") || n.Key.StartsWith(@"Software\Clients\StartMenuInternet\Zing"));
	}

	[Theory]
	[InlineData(null, false)]
	[InlineData("ChromeHTML", false)]
	[InlineData("ZingHTML", true)]
	public void Is_default_only_for_zing_prog_id(string? progId, bool expected)
	{
		Assert.Equal(expected, DefaultBrowserRegistration.IsDefault(progId));
	}

	[Fact]
	public void Keys_to_delete_are_only_zings()
	{
		var keys = DefaultBrowserRegistration.KeysToDelete();
		Assert.Equal(2, keys.Count);
		Assert.All(keys, k => Assert.True(k == ClassKey || k == ClientKey));
	}

	[Fact]
	public void A_link_with_query_and_space_survives_launch_args_whole()
	{
		const string url = "https://a.example/?x=1&y=2 3";
		Assert.Equal([url], LaunchArgs.Urls([url]));
	}

	/// <summary>Runs <paramref name="read"/> with ZING_E2E and ZING_DEFAULT_BROWSER set as given (null unsets), then puts both back.</summary>
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static string? WithEnvironment(string? e2e, string? seam, Func<string?> read)
	{
		var oldE2e = Environment.GetEnvironmentVariable("ZING_E2E");
		var oldSeam = Environment.GetEnvironmentVariable("ZING_DEFAULT_BROWSER");
		try
		{
			Environment.SetEnvironmentVariable("ZING_E2E", e2e);
			Environment.SetEnvironmentVariable("ZING_DEFAULT_BROWSER", seam);
			return read();
		}
		finally
		{
			Environment.SetEnvironmentVariable("ZING_E2E", oldE2e);
			Environment.SetEnvironmentVariable("ZING_DEFAULT_BROWSER", oldSeam);
		}
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public void Current_https_prog_id_comes_from_the_e2e_seam()
	{
		Assert.Equal("ChromeHTML", WithEnvironment("1", "ChromeHTML", DefaultBrowser.CurrentHttpsProgId));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public void An_e2e_run_without_the_seam_has_no_default_browser_and_never_reads_the_registry(string? seam)
	{
		Assert.Null(WithEnvironment("1", seam, DefaultBrowser.CurrentHttpsProgId));
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public void Seam_is_ignored_outside_e2e()
	{
		Assert.NotEqual("FakeSeamValue", WithEnvironment(null, "FakeSeamValue", DefaultBrowser.CurrentHttpsProgId));
	}

	[Theory]
	[InlineData(false, false, true)]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	[InlineData(true, true, false)]
	public void The_offer_waits_for_a_second_launch_and_stops_once_dismissed(bool dismissed, bool firstLaunch, bool allowed)
	{
		Assert.Equal(allowed, DefaultBrowserOffer.Allowed(dismissed, launchedBefore: !firstLaunch));
	}

	[Theory]
	[InlineData(true, false, true)]
	[InlineData(true, true, false)]
	[InlineData(false, false, false)]
	[InlineData(false, true, false)]
	public void A_launch_offers_only_an_installed_zing_that_is_not_the_default(bool registered, bool isDefault, bool wanted)
	{
		Assert.Equal(wanted, DefaultBrowserOffer.Wanted(new DefaultBrowserState(registered, isDefault)));
	}

	[Fact]
	public void The_offer_opens_windows_default_apps_at_zing()
	{
		Assert.Equal("ms-settings:defaultapps?registeredAppUser=Zing", DefaultBrowserOffer.SettingsUri);
		Assert.Equal("Make Zing your default browser?", DefaultBrowserOffer.Text);
	}
}

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

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public void Current_https_prog_id_comes_from_the_e2e_seam()
	{
		var e2e = Environment.GetEnvironmentVariable("ZING_E2E");
		var seam = Environment.GetEnvironmentVariable("ZING_DEFAULT_BROWSER");
		try
		{
			Environment.SetEnvironmentVariable("ZING_E2E", "1");
			Environment.SetEnvironmentVariable("ZING_DEFAULT_BROWSER", "ChromeHTML");
			Assert.Equal("ChromeHTML", DefaultBrowser.CurrentHttpsProgId());
		}
		finally
		{
			Environment.SetEnvironmentVariable("ZING_E2E", e2e);
			Environment.SetEnvironmentVariable("ZING_DEFAULT_BROWSER", seam);
		}
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public void Seam_is_ignored_outside_e2e()
	{
		var e2e = Environment.GetEnvironmentVariable("ZING_E2E");
		var seam = Environment.GetEnvironmentVariable("ZING_DEFAULT_BROWSER");
		try
		{
			Environment.SetEnvironmentVariable("ZING_E2E", null);
			Environment.SetEnvironmentVariable("ZING_DEFAULT_BROWSER", "FakeSeamValue");
			Assert.NotEqual("FakeSeamValue", DefaultBrowser.CurrentHttpsProgId());
		}
		finally
		{
			Environment.SetEnvironmentVariable("ZING_E2E", e2e);
			Environment.SetEnvironmentVariable("ZING_DEFAULT_BROWSER", seam);
		}
	}
}

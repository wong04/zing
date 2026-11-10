using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Zing.Core;

/// <summary>One registry value under HKCU. A null <paramref name="Name"/> is the key's default value.</summary>
public sealed record RegistryValue(string Key, string? Name, string Value);

/// <summary>What makes Zing appear in Windows' default-apps list. The host writes these from the install hooks.</summary>
public static class DefaultBrowserRegistration
{
	public const string ProgId = "ZingHTML";
	public const string ClientName = "Zing";

	private const string ClassKey = @"Software\Classes\" + ProgId;
	private const string ClientKey = @"Software\Clients\StartMenuInternet\" + ClientName;
	private const string CapabilitiesKey = ClientKey + @"\Capabilities";

	public static IReadOnlyList<RegistryValue> Values(string exePath)
	{
		var command = $"\"{exePath}\" \"%1\"";
		var icon = exePath + ",0";
		return
		[
			new(ClassKey, null, "Zing HTML Document"),
			new(ClassKey + @"\DefaultIcon", null, icon),
			new(ClassKey + @"\shell\open\command", null, command),
			new(ClientKey, null, ClientName),
			new(ClientKey + @"\DefaultIcon", null, icon),
			new(ClientKey + @"\shell\open\command", null, command),
			new(CapabilitiesKey, "ApplicationName", ClientName),
			new(CapabilitiesKey, "ApplicationDescription", "A private browser that keeps nothing by default."),
			new(CapabilitiesKey + @"\URLAssociations", "http", ProgId),
			new(CapabilitiesKey + @"\URLAssociations", "https", ProgId),
			new(CapabilitiesKey + @"\FileAssociations", ".htm", ProgId),
			new(CapabilitiesKey + @"\FileAssociations", ".html", ProgId),
			new(@"Software\RegisteredApplications", ClientName, CapabilitiesKey),
		];
	}

	/// <summary>The keys Zing owns. The RegisteredApplications value is shared, so the host removes it by name.</summary>
	public static IReadOnlyList<string> KeysToDelete() => [ClassKey, ClientKey];

	public static bool IsDefault(string? httpUserChoiceProgId) => httpUserChoiceProgId == ProgId;
}

public static class DefaultBrowser
{
	private const string HttpsUserChoiceKey = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";

	/// <summary>
	/// The ProgId Windows launches for https links, or null when the user never chose.
	/// Under ZING_E2E=1 a ZING_DEFAULT_BROWSER value stands in, so e2e never reads the real registry.
	/// </summary>
	[SupportedOSPlatform("windows")]
	public static string? CurrentHttpsProgId()
	{
		if (Environment.GetEnvironmentVariable("ZING_E2E") == "1")
		{
			var seam = Environment.GetEnvironmentVariable("ZING_DEFAULT_BROWSER");
			if (seam is not null) return seam;
		}
		using var key = Registry.CurrentUser.OpenSubKey(HttpsUserChoiceKey);
		return key?.GetValue("ProgId") as string;
	}
}

using Microsoft.Win32;
using Zing.Core.Import;

namespace Zing.Host;

/// <summary>E2e runs never read the registry: ZING_DEFAULT_BROWSER stands in, and unset means no default browser.</summary>
public sealed class DefaultBrowserReader : IDefaultBrowserReader
{
	private const string UserChoiceKey = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";

	public string? HttpsProgId()
	{
		if (TestHooks.IsEndToEndRun)
			return Environment.GetEnvironmentVariable("ZING_DEFAULT_BROWSER") is { Length: > 0 } configured ? configured : null;
		using var key = Registry.CurrentUser.OpenSubKey(UserChoiceKey);
		return key?.GetValue("ProgId") as string;
	}
}

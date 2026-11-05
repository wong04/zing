namespace Zing.Core;

/// <summary>Where Zing's own pages are served from: the only origin whose messages the host answers.</summary>
public static class ZingOrigin
{
	public const string Host = "app.zing";

	public static bool IsZingView(string source) => source.StartsWith($"https://{Host}/", StringComparison.Ordinal);
}

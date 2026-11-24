namespace Zing.Core.Mcp;

public static class AiUrls
{
	public static bool Allowed(Uri url) => url.Scheme is "http" or "https";
}

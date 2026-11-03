using System.Text.Json;

namespace Zing.Core.Net;

/// <summary>The pure parts of the engine, twins of src/shared/unblock/relayEngine.ts held to it by relayEngine.vectors.json.</summary>
public static class RelayRules
{
	private static readonly char[] PastedWrappers = ['<', '>', '"', '\''];

	/// <summary>Accepts the sloppy things people paste: bare domains, stray whitespace, links copied inside angle brackets.</summary>
	public static Uri Normalize(string input)
	{
		var text = input.Trim().Trim(PastedWrappers);
		if (text == "") throw new BadURLError();
		if (!text.Contains("://")) text = "https://" + text;
		if (!Uri.TryCreate(text, UriKind.Absolute, out var url)) throw new BadURLError();
		if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) throw new BadURLError();
		if (url.Host == "") throw new BadURLError();
		if (url.UserInfo != "") throw new CredentialsInURLError();
		return url;
	}

	/// <summary>Anything outside 200 to 399 is a failed fetch; redirects were already followed.</summary>
	public static void ValidateStatus(int status)
	{
		if (status < 200 || status >= 400) throw new HTTPStatusError(status);
	}

	/// <summary>
	/// The snapshot named by an archive.org/wayback/available answer. The API hands back http:// links that redirect, so only the
	/// snapshot's own scheme is upgraded; the archived page's address inside the path stays as it was.
	/// </summary>
	public static Uri WaybackSnapshot(JsonElement answer)
	{
		if (answer.ValueKind != JsonValueKind.Object
			|| !answer.TryGetProperty("archived_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object
			|| !snapshots.TryGetProperty("closest", out var closest) || closest.ValueKind != JsonValueKind.Object
			|| !closest.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True
			|| !closest.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
			throw new EmptyBodyError();
		var text = url.GetString()!;
		if (text.StartsWith("http://", StringComparison.Ordinal)) text = "https://" + text["http://".Length..];
		if (!Uri.TryCreate(text, UriKind.Absolute, out var snapshot)) throw new EmptyBodyError();
		return snapshot;
	}
}

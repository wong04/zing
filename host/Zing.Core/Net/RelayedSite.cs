using System.Net;
using System.Text;

namespace Zing.Core.Net;

public sealed record RelayedResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// Answers what a relayed page asks for. An address zing-relay://host/path stands for https://host/path, fetched by the
/// <see cref="RelayEngine"/>, so every rule of Open a Link holds for each file the page loads: no cookie or sign-in is
/// sent, the reader relay and the archive see an address only with the session's consent, and an address on the user's
/// own network never goes to them.
/// </summary>
public sealed class RelayedSite(IRelayTransport transport, Func<string> dohProvider, RelayConsent consent)
{
	/// <summary>
	/// A relayed page runs its scripts, but loads, connects (fetch, XHR, WebSocket), frames and posts forms only within the
	/// relay scheme, so an address its scripts build at run time or a stray http link can't reach the network directly.
	/// This does not cover WebRTC, which a content security policy can't restrict.
	/// </summary>
	public const string Policy = "default-src zing-relay: data: blob: 'unsafe-inline' 'unsafe-eval'; connect-src zing-relay:; form-action zing-relay:; frame-ancestors zing-relay:";

	public async Task<RelayedResponse> ServeAsync(string method, string url, bool isDocument, CancellationToken ct)
	{
		if (method != "GET") return Failure(405, "Zing only fetches pages through the relay; it doesn't send forms or other requests.", isDocument);
		if (RelayedContent.Decode(url) is not { } web) return Failure(400, "That isn't an address the relay can fetch.", isDocument);
		var engine = new RelayEngine(transport, dohProvider());
		RelayResult result;
		try
		{
			if (RelayedHosts.IsOwnNetworkHost(web.Host)) throw new OwnNetworkError();
			result = isDocument ? await engine.OpenAsync(web.AbsoluteUri, consent, ct) : await engine.OpenResourceAsync(web, consent, ct);
		}
		catch (OwnNetworkError error)
		{
			return Failure(403, error.Message, isDocument);
		}
		catch (RelayError error)
		{
			return Failure(502, error.Message, isDocument);
		}
		if (result.Outcome == RelayOutcome.NeedsConsent) return Failure(403, "Needs your OK to send this address to a third party. Use Open a Link in the Toolbox to give it.", isDocument);
		string? moved;
		try
		{
			moved = MovedTo(web, result);
		}
		catch (UnrelayableAddressError error)
		{
			return Failure(502, error.Message, isDocument);
		}
		if (moved is not null) return new RelayedResponse(302, Headers(("Location", moved)), []);
		var type = result.ContentType ?? "application/octet-stream";
		return new RelayedResponse(200, Headers(("Content-Type", type)), RelayedContent.Patch(result.Body, type));
	}

	/// <summary>Where the fetch ended up, when not where it was asked: relative links must resolve against that address. The reader relay's text is shown where it was asked.</summary>
	private static string? MovedTo(Uri asked, RelayResult result)
	{
		if (result.Strategy == RelayStrategy.ReaderRelay || result.FinalUrl is not { } final) return null;
		if (!Uri.TryCreate(final, UriKind.Absolute, out var finalUri) || finalUri.AbsoluteUri == asked.AbsoluteUri) return null;
		return (RelayedContent.Encode(finalUri) ?? throw new UnrelayableAddressError()).AbsoluteUri;
	}

	private static Dictionary<string, string> Headers(params (string Name, string Value)[] more)
	{
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["Content-Security-Policy"] = Policy,
			["Cache-Control"] = "no-store",
			["X-Content-Type-Options"] = "nosniff",
		};
		foreach (var (name, value) in more) headers[name] = value;
		return headers;
	}

	private static RelayedResponse Failure(int status, string message, bool isDocument)
	{
		if (!isDocument) return new RelayedResponse(status, Headers(("Content-Type", "text/plain; charset=utf-8")), Encoding.UTF8.GetBytes(message));
		var html = $"<!doctype html><meta charset=\"utf-8\"><title>Zing couldn't open this page</title><body style=\"font:16px system-ui;margin:3em auto;max-width:40em\"><h1>Zing couldn't open this page</h1><pre style=\"white-space:pre-wrap\">{WebUtility.HtmlEncode(message)}</pre>";
		return new RelayedResponse(status, Headers(("Content-Type", "text/html; charset=utf-8")), Encoding.UTF8.GetBytes(html));
	}
}

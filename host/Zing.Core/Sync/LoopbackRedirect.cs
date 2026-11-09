using System.Web;

namespace Zing.Core.Sync;

public enum LoopbackRequestKind { NotHttp, NotFound, WrongState, Code, Error }

/// <summary>One request to Google sign-in's loopback port: the code or Google's error for <see cref="LoopbackRequestKind.Code"/> and <see cref="LoopbackRequestKind.Error"/>.</summary>
public sealed record LoopbackRequest(LoopbackRequestKind Kind, string? Value = null);

/// <summary>
/// Reads a request head that reached Google sign-in's loopback port (Zing.Host.GoogleSignIn). The redirect is exactly
/// <c>GET /?code=…&amp;state=…</c> (or <c>error=</c>); another path, or another sign-in's state, is something else on this PC
/// knocking, which the listener answers and ignores.
/// </summary>
public static class LoopbackRedirect
{
	public static LoopbackRequest Read(string head, string state)
	{
		if (!head.Contains("\r\n\r\n", StringComparison.Ordinal)) return new(LoopbackRequestKind.NotHttp);
		var parts = head[..head.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');
		if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !parts[1].StartsWith('/')) return new(LoopbackRequestKind.NotHttp);

		var target = parts[1];
		var queryStart = target.IndexOf('?');
		var path = queryStart < 0 ? target : target[..queryStart];
		if (path != "/") return new(LoopbackRequestKind.NotFound);
		var query = HttpUtility.ParseQueryString(queryStart < 0 ? "" : target[(queryStart + 1)..]);
		if (query["code"] is null && query["error"] is null) return new(LoopbackRequestKind.NotFound);
		if (query["state"] != state) return new(LoopbackRequestKind.WrongState);
		return query["error"] is { } error ? new(LoopbackRequestKind.Error, error) : new(LoopbackRequestKind.Code, query["code"]);
	}
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Mcp;

/// <summary>
/// The Streamable HTTP sessions: <c>initialize</c> opens one with a random <c>Mcp-Session-Id</c>, every later request must
/// carry a live one, DELETE ends it. Unlike the rest of Zing.Core this is read on the listener's threads before a request
/// is queued to the UI thread, so its set is concurrent.
/// </summary>
public sealed class McpHttpSessions
{
	public enum Verdict { Handle, BadRequest, NotFound, MethodNotAllowed, Ended }

	/// <summary><see cref="NewSession"/> is set when the request is an <c>initialize</c>: the response carries it.</summary>
	public readonly record struct Admission(Verdict Verdict, string? NewSession = null);

	private readonly ConcurrentDictionary<string, byte> _live = new(StringComparer.Ordinal);

	public Admission Admit(string httpMethod, string? sessionId, string body)
	{
		switch (httpMethod)
		{
			case "DELETE":
				return new(sessionId is not null && _live.TryRemove(sessionId, out _) ? Verdict.Ended : Verdict.NotFound);
			case "POST":
				break;
			default:
				return new(Verdict.MethodNotAllowed);
		}
		if (IsInitialize(body))
		{
			var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
			_live[id] = 0;
			return new(Verdict.Handle, id);
		}
		if (sessionId is null) return new(Verdict.BadRequest);
		return new(_live.ContainsKey(sessionId) ? Verdict.Handle : Verdict.NotFound);
	}

	/// <summary>The token changed: every client must start over with the new one.</summary>
	public void DropAll() => _live.Clear();

	private static bool IsInitialize(string body)
	{
		try
		{
			return JsonNode.Parse(body) is JsonObject o && o["method"] is JsonValue m && m.TryGetValue<string>(out var method) && method == "initialize";
		}
		catch (JsonException)
		{
			// Not JSON: it needs a session like any request, and the handler answers it with a parse error.
			return false;
		}
	}
}

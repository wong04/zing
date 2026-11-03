using System.Net.Http.Headers;
using System.Text.Json;

namespace Zing.Core.Net;

public interface IDohClient
{
	/// <summary>The host's A records from one DoH provider, named by its settings value ("cloudflare", "cloudflare-security", "google", "quad9").</summary>
	Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct);
}

/// <summary>Asks a public resolver over HTTPS in the JSON dialect (port of src/shared/unblock/dohResolver.ts).</summary>
public sealed class DohClient : IDohClient
{
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

	// Quad9 serves its JSON API on 5053; the standard port speaks wire format only.
	private static readonly Dictionary<string, (string Name, string Endpoint)> Providers = new()
	{
		["cloudflare"] = ("Cloudflare", "https://cloudflare-dns.com/dns-query"),
		["cloudflare-security"] = ("Cloudflare (malware blocking)", "https://security.cloudflare-dns.com/dns-query"),
		["google"] = ("Google", "https://dns.google/resolve"),
		["quad9"] = ("Quad9", "https://dns.quad9.net:5053/dns-query"),
	};

	private readonly HttpClient _http;
	private readonly Func<string, string> _endpointOf;

	public DohClient(HttpClient http) : this(http, provider => Providers[provider].Endpoint)
	{
	}

	/// <param name="endpointOf">Where a known provider is asked; a test points every provider at a local server.</param>
	public DohClient(HttpClient http, Func<string, string> endpointOf)
	{
		_http = http;
		_endpointOf = endpointOf;
	}

	public async Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct)
	{
		if (!Providers.TryGetValue(provider, out var known)) throw new UnknownDohProviderError(provider);
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{_endpointOf(provider)}?name={Uri.EscapeDataString(host)}&type=A");
		// The Cloudflare endpoints answer 400 without this.
		if (provider.StartsWith("cloudflare", StringComparison.Ordinal)) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(Timeout);
		string body;
		try
		{
			using var response = await _http.SendAsync(request, deadline.Token);
			if (!response.IsSuccessStatusCode) throw new ResolverUnreachableError(known.Name, $"HTTP {(int)response.StatusCode}");
			body = await response.Content.ReadAsStringAsync(deadline.Token);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new ResolverUnreachableError(known.Name, "timed out");
		}
		catch (HttpRequestException e)
		{
			throw new ResolverUnreachableError(known.Name, (e.InnerException ?? e).Message, e);
		}
		return Parse(host, body);
	}

	private static IReadOnlyList<string> Parse(string host, string body)
	{
		JsonDocument document;
		try { document = JsonDocument.Parse(body); }
		catch (JsonException) { throw new MalformedDoHResponseError("not JSON"); }
		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code))
				throw new MalformedDoHResponseError("no Status field");
			var answers = ReadAnswers(root);
			// Status 3 is NXDOMAIN: the name doesn't exist upstream, which is different from a local resolver lying about it.
			if (code == 3) throw new NxDomainError(host);
			if (code != 0) throw new ResolverStatusError(code);
			// Type 1 is an A record; CNAMEs (type 5) come back in the same array and must not be connected to.
			return [.. answers.Where(a => a.Type == 1).Select(a => a.Data)];
		}
	}

	private static List<(int Type, string Data)> ReadAnswers(JsonElement root)
	{
		if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind == JsonValueKind.Null) return [];
		if (answers.ValueKind != JsonValueKind.Array) throw new MalformedDoHResponseError("Answer is not a list");
		var read = new List<(int, string)>();
		foreach (var answer in answers.EnumerateArray())
		{
			if (answer.ValueKind != JsonValueKind.Object
				|| !answer.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
				|| !answer.TryGetProperty("type", out var type) || !type.TryGetInt32(out var typeCode)
				|| !answer.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
				throw new MalformedDoHResponseError("an answer is missing name, type or data");
			read.Add((typeCode, data.GetString()!));
		}
		return read;
	}
}

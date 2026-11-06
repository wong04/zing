using System.Net.Http.Headers;
using Zing.Core.Net;

namespace Zing.Host;

/// <summary>
/// The network of an e2e run for Open a Link and relayed pages (TestHooks.NetServices): every request goes to a test's own
/// local server, never the internet. The server learns the address the engine wanted from X-Relay-Target and the route from
/// X-Relay-Via (plain or pinned), sees exactly the headers the engine sent, and may answer X-Relay-Final to say where the
/// request ended up after redirects.
/// </summary>
public sealed class LocalRelayTransport(Uri server) : IRelayTransport
{
	/// <summary>What the probes find when a test sets ZING_E2E_RELAY_URL alone: nothing, as no test here is about Diagnose.</summary>
	public const string NoProbes = "{\"system\":[],\"tcp\":false,\"tlsValid\":false}";

	private static readonly HttpClient Client = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });

	public Task<RelayResponse> GetAsync(RelayRequest request, CancellationToken ct) => FetchAsync(request, "plain", ct);

	public Task<RelayResponse> GetPinnedAsync(RelayRequest request, string dohProvider, CancellationToken ct) => FetchAsync(request, "pinned", ct);

	private async Task<RelayResponse> FetchAsync(RelayRequest request, string via, CancellationToken ct)
	{
		using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(server, request.Url.PathAndQuery));
		foreach (var (name, value) in request.Headers) message.Headers.TryAddWithoutValidation(name, value);
		message.Headers.TryAddWithoutValidation("X-Relay-Target", request.Url.AbsoluteUri);
		message.Headers.TryAddWithoutValidation("X-Relay-Via", via);
		using var response = await Client.SendAsync(message, ct);
		var final = response.Headers.TryGetValues("X-Relay-Final", out var values) ? new Uri(values.First()) : request.Url;
		return new RelayResponse((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct), Type(response.Content.Headers.ContentType), final);
	}

	private static string? Type(MediaTypeHeaderValue? type) => type?.ToString();
}

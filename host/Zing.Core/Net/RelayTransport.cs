using System.Net;
using System.Net.Http.Headers;

namespace Zing.Core.Net;

/// <param name="Headers">Everything the request sends besides the address. The transport adds nothing to it.</param>
public sealed record RelayRequest(Uri Url, IReadOnlyDictionary<string, string> Headers);

/// <param name="FinalUrl">Where the body came from after redirects.</param>
public sealed record RelayResponse(int Status, byte[] Body, string? ContentType, Uri FinalUrl);

/// <summary>How the engine reaches the network. A test supplies its own so none reaches the internet.</summary>
public interface IRelayTransport
{
	/// <summary>An ordinary request: the system's DNS, the system's proxy, redirects followed.</summary>
	Task<RelayResponse> GetAsync(RelayRequest request, CancellationToken ct);

	/// <summary>A request to the address a public DoH lookup gave, with the certificate checked against the real name.</summary>
	Task<RelayResponse> GetPinnedAsync(RelayRequest request, string dohProvider, CancellationToken ct);
}

public sealed class LiveRelayTransport : IRelayTransport
{
	private const int MaxRedirects = 5;

	private readonly IDohClient _doh;
	private readonly PinnedHttp _pinned;
	private readonly HttpClient _plain;

	public LiveRelayTransport(IDohClient doh, PinnedHttp? pinned = null)
	{
		_doh = doh;
		_pinned = pinned ?? new PinnedHttp();
		// No cookie jar, so nothing a site sets is sent back to the next one.
		_plain = new HttpClient(new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All, MaxAutomaticRedirections = MaxRedirects });
	}

	public async Task<RelayResponse> GetAsync(RelayRequest request, CancellationToken ct)
	{
		using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
		foreach (var (name, value) in request.Headers)
			if (!message.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException($"{name} isn't a request header.", nameof(request));
		using var response = await _plain.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
		return await ReadAsync(response, response.RequestMessage?.RequestUri ?? request.Url, ct);
	}

	public async Task<RelayResponse> GetPinnedAsync(RelayRequest request, string dohProvider, CancellationToken ct)
	{
		var url = request.Url;
		for (var hop = 0; hop <= MaxRedirects; hop++)
		{
			if (url.Scheme != Uri.UriSchemeHttps) throw new NotHttpsError();
			var address = await AddressForAsync(url, dohProvider, ct);
			using var response = await _pinned.GetAsync(url, address, ct, request.Headers);
			if (!IsRedirect(response.StatusCode)) return await ReadAsync(response, url, ct);
			url = response.Headers.Location is { } next ? new Uri(url, next) : throw new BadRedirectError();
		}
		throw new TooManyRedirectsError();
	}

	private async Task<IPAddress> AddressForAsync(Uri url, string dohProvider, CancellationToken ct)
	{
		if (IPAddress.TryParse(url.IdnHost.Trim('[', ']'), out var literal)) return literal;
		var found = await _doh.ResolveAsync(url.IdnHost, dohProvider, ct);
		if (found.Count == 0) throw new NoAddressError(url.IdnHost);
		return IPAddress.Parse(found[0]);
	}

	private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

	/// <summary>A 3xx that reaches here was not followed (another scheme, past the limit, no Location), so its body is not the page.</summary>
	private static async Task<RelayResponse> ReadAsync(HttpResponseMessage response, Uri finalUrl, CancellationToken ct)
	{
		var status = (int)response.StatusCode;
		if (status is >= 300 and < 400) throw new RedirectNotFollowedError();
		return new RelayResponse(status, await BoundedBody.ReadAsync(response.Content, ct), ContentType(response.Content.Headers.ContentType), finalUrl);
	}

	private static string? ContentType(MediaTypeHeaderValue? type) => type?.ToString();
}

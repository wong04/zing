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
	private readonly ConnectPolicy _policy;
	private readonly PinnedHttp _pinned;
	private readonly HttpClient _plain;

	public LiveRelayTransport(IDohClient doh, PinnedHttp? pinned = null) : this(doh, ConnectPolicy.Unrestricted, pinned)
	{
	}

	/// <param name="policy">Where this transport may connect; a transport for a relayed page's own requests passes <see cref="ConnectPolicy.PublicOnly"/>.</param>
	public LiveRelayTransport(IDohClient doh, ConnectPolicy policy, PinnedHttp? pinned = null)
	{
		_doh = doh;
		_policy = policy;
		_pinned = pinned ?? new PinnedHttp();
		// No cookie jar, so nothing a site sets is sent back to the next one.
		var handler = new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false };
		if (policy.ConnectCallback is { } connect)
		{
			handler.ConnectCallback = connect;
			// Behind a proxy the callback's endpoint is the proxy's, not the site's, and there would be nothing to check.
			handler.UseProxy = false;
		}
		_plain = new HttpClient(handler);
	}

	public async Task<RelayResponse> GetAsync(RelayRequest request, CancellationToken ct)
	{
		// Followed here, not by the handler: it would follow an ftp:// Location as if it were http on port 21.
		var url = request.Url;
		for (var hop = 0; hop <= MaxRedirects; hop++)
		{
			_policy.CheckHost(url.Host);
			using var message = new HttpRequestMessage(HttpMethod.Get, url);
			foreach (var (name, value) in request.Headers)
				if (!message.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException($"{name} isn't a request header.", nameof(request));
			using var response = await _plain.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
			if (!IsRedirect(response.StatusCode)) return await ReadAsync(response, url, ct);
			var next = NextHop(url, response);
			var downgrade = url.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp;
			if ((next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) || downgrade) throw new RedirectNotFollowedError();
			url = next;
		}
		throw new TooManyRedirectsError();
	}

	private static Uri NextHop(Uri from, HttpResponseMessage response)
	{
		if (!response.Headers.TryGetValues("Location", out var values) || values.FirstOrDefault() is not { } location) throw new BadRedirectError();
		return Uri.TryCreate(from, location, out var next) ? next : throw new BadRedirectError();
	}

	public async Task<RelayResponse> GetPinnedAsync(RelayRequest request, string dohProvider, CancellationToken ct)
	{
		var url = request.Url;
		for (var hop = 0; hop <= MaxRedirects; hop++)
		{
			if (url.Scheme != Uri.UriSchemeHttps) throw new NotHttpsError();
			_policy.CheckHost(url.Host);
			var address = await AddressForAsync(url, dohProvider, ct);
			_policy.CheckAddress(address);
			using var response = await _pinned.GetAsync(url, address, ct, request.Headers);
			if (!IsRedirect(response.StatusCode)) return await ReadAsync(response, url, ct);
			url = NextHop(url, response);
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

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Zing.Core.Net;

/// <summary>
/// HTTPS to an address chosen by the caller (one a DoH lookup returned) while the certificate is checked against the
/// URL's own host name, which is also the SNI. A certificate for any other name is refused.
/// </summary>
public sealed class PinnedHttp
{
	/// <summary>The most of a response that is read; a bigger one fails the request rather than filling memory.</summary>
	public const long MaxBodyBytes = 16 * 1024 * 1024;

	private readonly X509Certificate2Collection? _trustedRoots;

	/// <param name="trustedRoots">Roots trusted in place of the system's; null uses the system's. Tests serve a local certificate this way.</param>
	public PinnedHttp(X509Certificate2Collection? trustedRoots = null)
	{
		_trustedRoots = trustedRoots;
	}

	/// <summary>
	/// A redirect is returned, not followed: it may name another host, which needs its own lookup and pin.
	/// A certificate for the wrong name or from an untrusted issuer raises <see cref="AuthenticationException"/>.
	/// </summary>
	/// <param name="headers">Sent as given and nothing else is added: no cookies, no credentials.</param>
	public async Task<HttpResponseMessage> GetAsync(Uri url, IPAddress address, CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null)
	{
		if (url.Scheme != Uri.UriSchemeHttps) throw new ArgumentException($"A pinned request must be HTTPS, not {url.Scheme}.", nameof(url));
		var handler = new SocketsHttpHandler
		{
			UseCookies = false,
			// Behind a proxy the callback's endpoint is the proxy's, not the site's.
			UseProxy = false,
			AllowAutoRedirect = false,
			AutomaticDecompression = DecompressionMethods.All,
			ConnectCallback = (context, token) => ConnectAsync(address, context.DnsEndPoint.Port, token),
		};
		if (_trustedRoots is { } roots) handler.SslOptions.RemoteCertificateValidationCallback = CustomTrust.Callback(roots);
		using var http = new HttpClient(handler) { MaxResponseContentBufferSize = MaxBodyBytes };
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		foreach (var (name, value) in headers ?? new Dictionary<string, string>())
			if (!request.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException($"{name} isn't a request header.", nameof(headers));
		try
		{
			// Buffered, so the response outlives the handler that is disposed here.
			return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
		}
		catch (HttpRequestException e) when (e.InnerException is AuthenticationException refused)
		{
			ExceptionDispatchInfo.Capture(refused).Throw();
			throw;
		}
	}

	private static async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct)
	{
		var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
		try
		{
			await socket.ConnectAsync(new IPEndPoint(address, port), ct);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}
}

/// <summary>Certificate checks against roots other than the system's.</summary>
internal static class CustomTrust
{
	/// <summary>Whether the certificate chains to one of <paramref name="roots"/>. A name mismatch the handshake reported always fails.</summary>
	public static bool Accepts(X509Certificate2Collection roots, X509Certificate? certificate, SslPolicyErrors errors)
	{
		if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
		using var chain = new X509Chain();
		chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
		chain.ChainPolicy.CustomTrustStore.AddRange(roots);
		chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
		using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
		return chain.Build(leaf);
	}

	public static RemoteCertificateValidationCallback Callback(X509Certificate2Collection roots) =>
		(_, certificate, _, errors) => Accepts(roots, certificate, errors);
}

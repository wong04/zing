using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Zing.Core.Net;

/// <param name="HandshakeCompleted">The TLS handshake finished.</param>
/// <param name="CertificateValid">It finished and the certificate chains to a trusted root and is valid for the host name.</param>
/// <param name="Failure">Why the certificate was refused, or why the handshake failed.</param>
public sealed record TlsProbeResult(bool HandshakeCompleted, bool CertificateValid, string? Failure = null);

/// <summary>The network checks a diagnosis makes, behind an interface so tests can fake the network.</summary>
public interface IProbes
{
	/// <summary>The host's IPv4 addresses from the system resolver, i.e. whatever DNS server the network handed out. Empty when it has no answer.</summary>
	Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct);

	/// <summary>Whether a TCP connection to the address on the HTTPS port is accepted before the deadline.</summary>
	Task<bool> TcpAsync(string ip, CancellationToken ct);

	/// <summary>
	/// Connects to <paramref name="ip"/> presenting <paramref name="host"/> as the SNI, which is what a filter keys on, and checks
	/// the certificate against the host. Dialling an address other than the host's own is the point: can this address prove it is that host.
	/// </summary>
	Task<TlsProbeResult> TlsAsync(string ip, string host, CancellationToken ct);
}

public sealed class Probes : IProbes
{
	/// <summary>A dropped-packet block shows up as an indefinite hang, so every probe has its own deadline.</summary>
	public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(6);

	private readonly int _port;
	private readonly TimeSpan _deadline;
	private readonly X509Certificate2Collection? _trustedRoots;

	/// <param name="port">The port probed; a test points it at a local server.</param>
	/// <param name="trustedRoots">Roots trusted in place of the system's; null uses the system's.</param>
	public Probes(int port = 443, TimeSpan? deadline = null, X509Certificate2Collection? trustedRoots = null)
	{
		_port = port;
		_deadline = deadline ?? DefaultDeadline;
		_trustedRoots = trustedRoots;
	}

	public async Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct)
	{
		try
		{
			var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
			return [.. addresses.Select(a => a.ToString()).Distinct()];
		}
		// A resolver that refuses or has no answer is an empty answer; that is itself a signal. Anything else is raised.
		catch (SocketException e) when (e.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
		{
			return [];
		}
	}

	public async Task<bool> TcpAsync(string ip, CancellationToken ct)
	{
		using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(_deadline);
		try
		{
			await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), _port), deadline.Token);
			return true;
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			return false;
		}
		catch (SocketException)
		{
			return false;
		}
	}

	public async Task<TlsProbeResult> TlsAsync(string ip, string host, CancellationToken ct)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(_deadline);
		SslPolicyErrors? errors = null;
		var valid = false;
		try
		{
			using var client = new TcpClient();
			await client.ConnectAsync(IPAddress.Parse(ip), _port, deadline.Token);
			await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, policyErrors) =>
			{
				// Verification failures must not abort the connection: a handshake that completes with a bad certificate is a different finding from one that dies.
				errors = policyErrors;
				valid = _trustedRoots is { } roots ? CustomTrust.Accepts(roots, certificate, policyErrors) : policyErrors == SslPolicyErrors.None;
				return true;
			});
			await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, deadline.Token);
			return new TlsProbeResult(true, valid, valid ? null : $"certificate refused ({errors})");
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			return new TlsProbeResult(false, false, "timed out");
		}
		catch (Exception e) when (e is SocketException or IOException or AuthenticationException)
		{
			return new TlsProbeResult(false, false, (e.InnerException ?? e).Message);
		}
	}
}

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Zing.Core.Tests.Net;

/// <summary>A throwaway certificate authority, so a test can serve a certificate that is valid for one name and not another.</summary>
internal sealed class TestPki : IDisposable
{
	private readonly X509Certificate2 _root;

	public TestPki()
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest("CN=Zing test root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
		request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
		using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
		_root = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
	}

	/// <summary>Only the public part: what a client is told to trust.</summary>
	public X509Certificate2Collection Trusted => [X509CertificateLoader.LoadCertificate(_root.RawData)];

	public X509Certificate2 IssueFor(string dnsName)
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		var names = new SubjectAlternativeNameBuilder();
		names.AddDnsName(dnsName);
		request.CertificateExtensions.Add(names.Build());
		request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
		request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
		using var signed = request.Create(_root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(8));
		using var withKey = signed.CopyWithPrivateKey(key);
		// Round-tripped through a PFX so the key outlives the temporary one, which SslStream needs on Windows.
		return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
	}

	/// <summary>A certificate nothing trusts.</summary>
	public static X509Certificate2 SelfSigned(string dnsName)
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
		return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
	}

	public void Dispose() => _root.Dispose();
}

/// <summary>
/// A loopback server. With a certificate it speaks HTTPS and answers every request with <paramref name="response"/>;
/// without one it accepts connections and hangs up.
/// </summary>
internal sealed class TestServer : IDisposable
{
	public const string Hi = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi";

	private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
	private readonly CancellationTokenSource _stop = new();
	private readonly X509Certificate2? _certificate;
	private readonly string _response;

	public TestServer(X509Certificate2? certificate = null, string response = Hi)
	{
		_certificate = certificate;
		_response = response;
		_listener.Start();
		_ = Task.Run(AcceptAsync);
	}

	/// <summary>The last request's head, as the client sent it.</summary>
	public string LastRequest { get; private set; } = "";

	public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

	private async Task AcceptAsync()
	{
		while (!_stop.IsCancellationRequested)
		{
			TcpClient client;
			try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
			catch (OperationCanceledException) { return; }
			_ = Task.Run(() => ServeAsync(client));
		}
	}

	private async Task ServeAsync(TcpClient client)
	{
		using var _ = client;
		if (_certificate is null) return;
		try
		{
			await using var tls = new SslStream(client.GetStream());
			await tls.AuthenticateAsServerAsync(_certificate);
			var buffer = new byte[4096];
			var seen = new StringBuilder();
			while (!seen.ToString().Contains("\r\n\r\n"))
			{
				var read = await tls.ReadAsync(buffer);
				if (read == 0) return;
				seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
			}
			LastRequest = seen.ToString();
			await tls.WriteAsync(Encoding.ASCII.GetBytes(_response));
		}
		// A client that refuses the certificate drops the connection mid-handshake; that is what those tests are about.
		catch (Exception e) when (e is IOException or AuthenticationException or ObjectDisposedException) { }
	}

	public void Dispose()
	{
		_stop.Cancel();
		_listener.Stop();
		_certificate?.Dispose();
	}
}
